using System;
using System.Threading;
using System.Threading.Tasks;
using ASFNotify.Configuration;
using ASFNotify.Data;

namespace ASFNotify.Notifiers;

internal sealed class GotifyNotifier : INotifier {
	public string Name => "Gotify";

	public bool IsConfigured(PluginConfig config) => config.Gotify?.IsValid == true;

	public Task<bool> SendAsync(PluginConfig config, NotificationEvent notification, CancellationToken cancellationToken = default) {
		GotifyConfig gotify = config.Gotify!;

		Uri endpoint = new(gotify.Url!, "message");
		UriBuilder builder = new(endpoint) { Query = $"token={Uri.EscapeDataString(gotify.Token!)}" };

		return NotifierHttp.PostJsonAsync(
			builder.Uri,
			writer => {
				writer.WriteString("title", notification.Title);
				writer.WriteString("message", notification.Message);
				writer.WriteNumber("priority", MapPriority(notification.Priority));
			},
			null,
			cancellationToken
		);
	}

	// Gotify scale: 0 .. 10.
	private static int MapPriority(ENotificationPriority priority) => priority switch {
		ENotificationPriority.Low => 2,
		ENotificationPriority.High => 8,
		_ => 5
	};
}
