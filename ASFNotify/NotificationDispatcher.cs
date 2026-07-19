using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ASFNotify.Configuration;
using ASFNotify.Data;
using ASFNotify.Notifiers;

namespace ASFNotify;

// Keeps network I/O out of ASF's event handlers: they enqueue and return, a single background
// consumer delivers. Applies the per-(bot, event) cooldown and one retry.
internal sealed class NotificationDispatcher {
	private const int QueueCapacity = 64;

	// Each send attempt gets its own budget so one hung backend can't starve the others; the overall
	// budget caps a single notification's total time across all backends and the retry.
	private const int PerAttemptTimeoutSeconds = 10;
	private const int OverallDispatchTimeoutSeconds = 45;
	private const int RetryDelaySeconds = 3;

	private readonly Channel<QueueItem> Channel = System.Threading.Channels.Channel.CreateBounded<QueueItem>(
		new BoundedChannelOptions(QueueCapacity) {
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true
		},
		static dropped => ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Notification queue is full, dropped {dropped.Notification.Type} for {dropped.Notification.BotName}.")
	);

	private readonly ConcurrentDictionary<(string BotName, EEventType Type), DateTime> LastSent = new();
	private readonly ImmutableArray<INotifier> Notifiers;

	private readonly Task ConsumerTask;

	internal NotificationDispatcher(ImmutableArray<INotifier> notifiers) {
		Notifiers = notifiers;
		ConsumerTask = Task.Run(ConsumeAsync);
	}

	// Non-blocking; safe from any handler.
	internal void Enqueue(NotificationEvent notification, PluginConfig config) {
		if (!notification.BypassCooldown && IsOnCooldown(notification, config.EffectiveCooldownMinutes)) {
			return;
		}

		// DropOldest means TryWrite never fails; the drop callback logs any eviction.
		Channel.Writer.TryWrite(new QueueItem(notification, config));
	}

	private async Task ConsumeAsync() {
		try {
			await foreach (QueueItem item in Channel.Reader.ReadAllAsync().ConfigureAwait(false)) {
				try {
					await DispatchAsync(item).ConfigureAwait(false);
				} catch (Exception e) {
					// A failing notifier must never take down the consumer loop.
					ASF.ArchiLogger.LogGenericWarningException(e);
				}
			}
		} catch (Exception e) {
			// The consumer dying would silently stop all future notifications; make that visible.
			ASF.ArchiLogger.LogGenericException(e);
		}
	}

	private async Task DispatchAsync(QueueItem item) {
		// Re-check the cooldown here: a burst can race several events past the read-only Enqueue check
		// before the first one delivers and stamps, so collapse them to one delivery per window.
		if (!item.Notification.BypassCooldown && IsOnCooldown(item.Notification, item.Config.EffectiveCooldownMinutes)) {
			return;
		}

		using CancellationTokenSource overall = new(TimeSpan.FromSeconds(OverallDispatchTimeoutSeconds));

		bool delivered = false;

		foreach (INotifier notifier in Notifiers) {
			if (!notifier.IsConfigured(item.Config)) {
				continue;
			}

			if (overall.IsCancellationRequested) {
				ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Dispatch budget exhausted, skipped {notifier.Name} for {item.Notification.Type}/{item.Notification.BotName}.");

				continue;
			}

			SendOutcome outcome = await TrySendAsync(notifier, item, overall.Token).ConfigureAwait(false);

			if ((outcome == SendOutcome.Failed) && !overall.IsCancellationRequested) {
				// One retry after a short delay; an immediate retry just re-hits the same connect failure.
				try {
					await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), overall.Token).ConfigureAwait(false);

					outcome = await TrySendAsync(notifier, item, overall.Token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					outcome = SendOutcome.TimedOut;
				}
			}

			switch (outcome) {
				case SendOutcome.Delivered:
					delivered = true;

					break;
				case SendOutcome.TimedOut:
					ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] {notifier.Name} timed out sending {item.Notification.Type} for {item.Notification.BotName}.");

					break;
				default:
					ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] {notifier.Name} failed to deliver {item.Notification.Type} for {item.Notification.BotName}.");

					break;
			}
		}

		// Start the cooldown only once something was actually delivered, so a failed send doesn't
		// suppress the next (possibly important) repeat for the whole window.
		if (delivered) {
			LastSent[(item.Notification.BotName, item.Notification.Type)] = DateTime.UtcNow;
		}
	}

	private static async Task<SendOutcome> TrySendAsync(INotifier notifier, QueueItem item, CancellationToken overallToken) {
		using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
		attempt.CancelAfter(TimeSpan.FromSeconds(PerAttemptTimeoutSeconds));

		try {
			return await notifier.SendAsync(item.Config, item.Notification, attempt.Token).ConfigureAwait(false) ? SendOutcome.Delivered : SendOutcome.Failed;
		} catch (OperationCanceledException) {
			return SendOutcome.TimedOut;
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			return SendOutcome.Failed;
		}
	}

	// Read-only; the timestamp is written after a successful delivery in DispatchAsync.
	private bool IsOnCooldown(NotificationEvent notification, byte cooldownMinutes) {
		if (cooldownMinutes == 0) {
			return false;
		}

		return LastSent.TryGetValue((notification.BotName, notification.Type), out DateTime last) && (DateTime.UtcNow - last < TimeSpan.FromMinutes(cooldownMinutes));
	}

	private enum SendOutcome : byte {
		Delivered,
		Failed,
		TimedOut
	}

	private sealed record QueueItem(NotificationEvent Notification, PluginConfig Config);
}
