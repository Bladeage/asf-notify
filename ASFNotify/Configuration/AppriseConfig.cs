using System;
using System.Text.Json;

namespace ASFNotify.Configuration;

// Apprise (apprise-api): posts to a persistent /notify/{key} endpoint that fans out to the services
// configured there. Tags optionally target a subset of them.
internal sealed record AppriseConfig {
	internal Uri? Url { get; init; }
	internal string? Tags { get; init; }

	internal bool IsValid => Url != null;

	internal static AppriseConfig FromJson(JsonElement element) => new() {
		Url = element.GetUriOrNull("Url"),
		Tags = element.GetStringOrNull("Tags")
	};
}
