using System.Threading;
using System.Threading.Tasks;
using ASFNotify.Configuration;
using ASFNotify.Data;

namespace ASFNotify.Notifiers;

internal sealed class AppriseNotifier : INotifier {
	public string Name => "Apprise";

	public bool IsConfigured(PluginConfig config) => config.Apprise?.IsValid == true;

	public Task<bool> SendAsync(PluginConfig config, NotificationEvent notification, CancellationToken cancellationToken = default) {
		AppriseConfig apprise = config.Apprise!;

		return NotifierHttp.PostJsonAsync(
			apprise.Url!,
			writer => {
				writer.WriteString("title", notification.Title);
				writer.WriteString("body", notification.Message);
				writer.WriteString("type", MapType(notification.Type));

				if (!string.IsNullOrEmpty(apprise.Tags)) {
					writer.WriteString("tag", apprise.Tags);
				}
			},
			null,
			cancellationToken
		);
	}

	// Apprise type drives the message colour/icon.
	private static string MapType(EEventType type) => type switch {
		EEventType.LoggedOn => "success",
		EEventType.FarmingStarted => "info",
		EEventType.FarmingFinished => "success",
		EEventType.TradeAccepted => "success",
		EEventType.GiftReceived => "success",
		EEventType.GameRedeemed => "success",
		EEventType.BotAdded => "success",
		EEventType.AsfStarted => "success",
		EEventType.Disconnected => "warning",
		EEventType.FarmingStopped => "warning",
		EEventType.TradeOffer => "info",
		EEventType.TradeRefused => "warning",
		EEventType.BotRemoved => "warning",
		EEventType.AsfUpdated => "info",
		EEventType.PluginUpdated => "info",
		EEventType.LoginAttention => "failure",
		EEventType.AccountAlert => "failure",
		_ => "info"
	};
}
