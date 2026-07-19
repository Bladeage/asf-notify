using System;
using System.Collections.Generic;
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

		// Append "message" to the path only. Using GetLeftPart(Path) preserves a reverse-proxy subpath
		// (https://host/gotify) while dropping any query/fragment, which new Uri(base, "message") or
		// AbsoluteUri would mishandle.
		Uri endpoint = new($"{gotify.Url!.GetLeftPart(UriPartial.Path).TrimEnd('/')}/message");

		// The token goes into a header rather than the URL, where it would end up in ASF's Debug-level
		// HTTP traces and any proxy logs along the way.
		return NotifierHttp.PostJsonAsync(
			endpoint,
			writer => {
				writer.WriteString("title", notification.Title);
				writer.WriteString("message", notification.Message);
				writer.WriteNumber("priority", MapPriority(notification.Priority));
			},
			[new KeyValuePair<string, string>("X-Gotify-Key", gotify.Token!)],
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
