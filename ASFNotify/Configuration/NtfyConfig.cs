using System;
using System.Text.Json;

namespace ASFNotify.Configuration;

// ntfy: the topic is the last path segment of Url (e.g. https://ntfy.sh/my-topic).
internal sealed record NtfyConfig {
	internal Uri? Url { get; init; }
	internal string? Token { get; init; }

	internal bool IsValid => (Url != null) && !string.IsNullOrEmpty(Url.AbsolutePath.Trim('/'));

	internal static NtfyConfig FromJson(JsonElement element) => new() {
		Url = element.GetUriOrNull("Url"),
		Token = element.GetStringOrNull("Token")
	};
}
