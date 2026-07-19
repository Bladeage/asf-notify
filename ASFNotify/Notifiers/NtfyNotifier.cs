using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ASFNotify.Configuration;
using ASFNotify.Data;

namespace ASFNotify.Notifiers;

internal sealed class NtfyNotifier : INotifier {
	public string Name => "ntfy";

	public bool IsConfigured(PluginConfig config) => config.Ntfy?.IsValid == true;

	public Task<bool> SendAsync(PluginConfig config, NotificationEvent notification, CancellationToken cancellationToken = default) {
		NtfyConfig ntfy = config.Ntfy!;
		Uri url = ntfy.Url!;

		// Use the JSON publish endpoint (POST to the server root with the topic in the body) so non-ASCII
		// titles survive - HTTP headers would be latin-1. The topic is the LAST path segment; anything
		// before it is a reverse-proxy subpath and stays part of the base URL.
		string path = url.AbsolutePath.Trim('/');
		int lastSlash = path.LastIndexOf('/');
		string topic = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
		string authority = url.GetLeftPart(UriPartial.Authority);
		Uri baseUrl = new(lastSlash >= 0 ? $"{authority}/{path[..lastSlash]}" : authority);

		IReadOnlyCollection<KeyValuePair<string, string>>? headers = string.IsNullOrEmpty(ntfy.Token)
			? null
			: [new KeyValuePair<string, string>("Authorization", $"Bearer {ntfy.Token}")];

		return NotifierHttp.PostJsonAsync(
			baseUrl,
			writer => {
				writer.WriteString("topic", topic);
				writer.WriteString("title", notification.Title);
				writer.WriteString("message", notification.Message);
				writer.WriteNumber("priority", MapPriority(notification.Priority));
				writer.WriteStartArray("tags");
				writer.WriteStringValue(MapTag(notification.Type));
				writer.WriteEndArray();
			},
			headers,
			cancellationToken
		);
	}

	// ntfy scale: 1 (min) .. 5 (max), 3 = default.
	private static int MapPriority(ENotificationPriority priority) => priority switch {
		ENotificationPriority.Low => 2,
		ENotificationPriority.High => 5,
		_ => 3
	};

	private static string MapTag(EEventType type) => type switch {
		EEventType.LoggedOn => "white_check_mark",
		EEventType.LoginAttention => "rotating_light",
		EEventType.Disconnected => "warning",
		EEventType.GameFarmingStarted => "seedling",
		EEventType.GameFarmingFinished => "black_joker",
		EEventType.MassFarmingStarted => "arrow_double_up",
		EEventType.FarmingFinished => "tada",
		EEventType.FarmingStopped => "octagonal_sign",
		EEventType.TradeOffer => "handshake",
		EEventType.TradeAccepted => "white_check_mark",
		EEventType.TradeRefused => "no_entry",
		EEventType.GiftReceived => "gift",
		EEventType.AccountAlert => "rotating_light",
		EEventType.GameRedeemed => "video_game",
		EEventType.BotAdded => "heavy_plus_sign",
		EEventType.BotRemoved => "heavy_minus_sign",
		EEventType.AsfStarted => "rocket",
		EEventType.AsfUpdated => "arrow_up",
		EEventType.PluginUpdated => "arrow_up",
		_ => "bell"
	};
}
