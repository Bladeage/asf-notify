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

	// Whole-dispatch budget for one notification across all backends and the retry, so one dead
	// backend can't stall the single consumer for minutes.
	private const int DispatchTimeoutSeconds = 25;
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
		if (IsOnCooldown(notification, config.EffectiveCooldownMinutes)) {
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
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DispatchTimeoutSeconds));

		bool delivered = false;

		foreach (INotifier notifier in Notifiers) {
			if (!notifier.IsConfigured(item.Config)) {
				continue;
			}

			bool ok = await TrySendAsync(notifier, item, cts.Token).ConfigureAwait(false);

			if (!ok && !cts.IsCancellationRequested) {
				// One retry after a short delay; an immediate retry just re-hits the same connect failure.
				try {
					await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), cts.Token).ConfigureAwait(false);

					ok = await TrySendAsync(notifier, item, cts.Token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					// Out of time for this item.
				}
			}

			if (ok) {
				delivered = true;
			} else {
				ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] {notifier.Name} failed to deliver {item.Notification.Type} for {item.Notification.BotName}.");
			}
		}

		// Start the cooldown only once something was actually delivered, so a failed send doesn't
		// suppress the next (possibly important) repeat for the whole window.
		if (delivered) {
			LastSent[(item.Notification.BotName, item.Notification.Type)] = DateTime.UtcNow;
		}
	}

	private static async Task<bool> TrySendAsync(INotifier notifier, QueueItem item, CancellationToken cancellationToken) {
		try {
			return await notifier.SendAsync(item.Config, item.Notification, cancellationToken).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return false;
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			return false;
		}
	}

	// Read-only check; the timestamp is written after a successful delivery in DispatchAsync.
	private bool IsOnCooldown(NotificationEvent notification, byte cooldownMinutes) {
		if (cooldownMinutes == 0) {
			return false;
		}

		return LastSent.TryGetValue((notification.BotName, notification.Type), out DateTime last) && (DateTime.UtcNow - last < TimeSpan.FromMinutes(cooldownMinutes));
	}

	private sealed record QueueItem(NotificationEvent Notification, PluginConfig Config);
}
