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

	private readonly Channel<QueueItem> Channel = System.Threading.Channels.Channel.CreateBounded<QueueItem>(
		new BoundedChannelOptions(QueueCapacity) {
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true
		}
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

		if (!Channel.Writer.TryWrite(new QueueItem(notification, config))) {
			ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Notification queue is full, dropped {notification.Type} for {notification.BotName}.");
		}
	}

	private async Task ConsumeAsync() {
		await foreach (QueueItem item in Channel.Reader.ReadAllAsync().ConfigureAwait(false)) {
			try {
				await DispatchAsync(item).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericWarningException(e);
			}
		}
	}

	private async Task DispatchAsync(QueueItem item) {
		foreach (INotifier notifier in Notifiers) {
			if (!notifier.IsConfigured(item.Config)) {
				continue;
			}

			bool delivered = await TrySendAsync(notifier, item).ConfigureAwait(false);

			if (!delivered) {
				delivered = await TrySendAsync(notifier, item).ConfigureAwait(false);
			}

			if (!delivered) {
				ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] {notifier.Name} failed to deliver {item.Notification.Type} for {item.Notification.BotName}.");
			}
		}
	}

	private static async Task<bool> TrySendAsync(INotifier notifier, QueueItem item) {
		try {
			return await notifier.SendAsync(item.Config, item.Notification).ConfigureAwait(false);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			return false;
		}
	}

	private bool IsOnCooldown(NotificationEvent notification, byte cooldownMinutes) {
		if (cooldownMinutes == 0) {
			return false;
		}

		TimeSpan cooldown = TimeSpan.FromMinutes(cooldownMinutes);
		DateTime now = DateTime.UtcNow;
		(string BotName, EEventType Type) key = (notification.BotName, notification.Type);

		while (true) {
			if (LastSent.TryGetValue(key, out DateTime last)) {
				if (now - last < cooldown) {
					return true;
				}

				if (LastSent.TryUpdate(key, now, last)) {
					return false;
				}
			} else if (LastSent.TryAdd(key, now)) {
				return false;
			}
		}
	}

	private sealed record QueueItem(NotificationEvent Notification, PluginConfig Config);
}
