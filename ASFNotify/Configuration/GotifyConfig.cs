using System;
using System.Text.Json;

namespace ASFNotify.Configuration;

// Gotify: posts to {Url}/message?token={Token}.
internal sealed record GotifyConfig {
	internal Uri? Url { get; init; }
	internal string? Token { get; init; }

	internal bool IsValid => (Url != null) && !string.IsNullOrEmpty(Token);

	internal static GotifyConfig FromJson(JsonElement element) => new() {
		Url = element.GetUriOrNull("Url"),
		Token = element.GetStringOrNull("Token")
	};
}
