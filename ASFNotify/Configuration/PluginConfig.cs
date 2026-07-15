using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;
using ArchiSteamFarm.Core;
using ASFNotify.Data;

namespace ASFNotify.Configuration;

// Read from the "ASFNotify" key in ASF.json (global) and/or a bot config (per-bot override).
internal sealed record PluginConfig {
	internal const byte DefaultCooldownMinutes = 5;

	// Low-noise defaults when "Events" is omitted; everything else is opt-in.
	internal static readonly ImmutableHashSet<EEventType> DefaultEvents = [
		EEventType.Disconnected,
		EEventType.LoginAttention,
		EEventType.FarmingFinished,
		EEventType.GiftReceived,
		EEventType.AccountAlert,
		EEventType.AsfStarted,
		EEventType.AsfUpdated,
		EEventType.PluginUpdated
	];

	internal AppriseConfig? Apprise { get; init; }

	// Minutes between two notifications for the same (bot, event). 0 disables the cooldown.
	internal byte? CooldownMinutes { get; init; }

	// Event names to report; parsed against EEventType. Null means DefaultEvents.
	internal IReadOnlyCollection<string>? Events { get; init; }

	internal GotifyConfig? Gotify { get; init; }

	internal NtfyConfig? Ntfy { get; init; }

	// Optional per-event message overrides. Placeholders: {Bot}, {SteamID}, {Reason}, {FarmedSomething}.
	internal IReadOnlyDictionary<string, string>? Templates { get; init; }

	internal byte EffectiveCooldownMinutes => CooldownMinutes ?? DefaultCooldownMinutes;

	internal ImmutableHashSet<EEventType> EffectiveEvents {
		get {
			if (Events == null) {
				return DefaultEvents;
			}

			HashSet<EEventType> parsed = [];

			foreach (string name in Events) {
				if (Enum.TryParse(name, true, out EEventType type)) {
					parsed.Add(type);
				}
			}

			return [.. parsed];
		}
	}

	internal bool HasAnyBackend => (Ntfy?.IsValid == true) || (Gotify?.IsValid == true) || (Apprise?.IsValid == true);

	internal static PluginConfig FromJson(JsonElement root) {
		List<string>? events = null;

		if (root.TryGetPropertyCI("Events", out JsonElement eventsElement)) {
			if (eventsElement.ValueKind == JsonValueKind.Array) {
				events = [];

				foreach (JsonElement item in eventsElement.EnumerateArray()) {
					if (item.ValueKind != JsonValueKind.String) {
						ASF.ArchiLogger.LogGenericWarning("[ASFNotify] Ignoring a non-string entry in \"Events\".");

						continue;
					}

					string? name = item.GetString();

					if (string.IsNullOrEmpty(name)) {
						continue;
					}

					events.Add(name);

					if (!Enum.TryParse(name, true, out EEventType parsed) || !Enum.IsDefined(parsed)) {
						ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Ignoring unknown event name in \"Events\": {name}");
					}
				}
			} else if (eventsElement.ValueKind != JsonValueKind.Null) {
				ASF.ArchiLogger.LogGenericWarning("[ASFNotify] \"Events\" must be an array of event names; ignoring it (default events apply).");
			}
		}

		Dictionary<string, string>? templates = null;

		if (root.TryGetPropertyCI("Templates", out JsonElement templatesElement)) {
			if (templatesElement.ValueKind == JsonValueKind.Object) {
				templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

				foreach (JsonProperty property in templatesElement.EnumerateObject()) {
					if (property.Value.ValueKind != JsonValueKind.String) {
						ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Ignoring non-string template for event: {property.Name}");

						continue;
					}

					templates[property.Name] = property.Value.GetString() ?? string.Empty;

					if (!Enum.TryParse(property.Name, true, out EEventType parsed) || !Enum.IsDefined(parsed)) {
						ASF.ArchiLogger.LogGenericWarning($"[ASFNotify] Ignoring template for unknown event: {property.Name}");
					}
				}
			} else if (templatesElement.ValueKind != JsonValueKind.Null) {
				ASF.ArchiLogger.LogGenericWarning("[ASFNotify] \"Templates\" must be an object keyed by event name; ignoring it.");
			}
		}

		byte? cooldownMinutes = root.GetByteOrNull("CooldownMinutes");

		if ((cooldownMinutes == null) && root.TryGetPropertyCI("CooldownMinutes", out JsonElement cooldownElement) && (cooldownElement.ValueKind != JsonValueKind.Null)) {
			ASF.ArchiLogger.LogGenericWarning("[ASFNotify] CooldownMinutes must be a whole number 0-255; the value will be ignored.");
		}

		return new PluginConfig {
			Ntfy = root.TryGetPropertyCI("Ntfy", out JsonElement ntfy) && (ntfy.ValueKind == JsonValueKind.Object) ? NtfyConfig.FromJson(ntfy) : null,
			Gotify = root.TryGetPropertyCI("Gotify", out JsonElement gotify) && (gotify.ValueKind == JsonValueKind.Object) ? GotifyConfig.FromJson(gotify) : null,
			Apprise = root.TryGetPropertyCI("Apprise", out JsonElement apprise) && (apprise.ValueKind == JsonValueKind.Object) ? AppriseConfig.FromJson(apprise) : null,
			Events = events,
			CooldownMinutes = cooldownMinutes,
			Templates = templates
		};
	}

	// Per-bot values win over global; a per-bot Events/Templates replaces (doesn't merge into) global.
	internal static PluginConfig? Merge(PluginConfig? global, PluginConfig? perBot) {
		if (global == null) {
			return perBot;
		}

		if (perBot == null) {
			return global;
		}

		return new PluginConfig {
			Apprise = perBot.Apprise ?? global.Apprise,
			CooldownMinutes = perBot.CooldownMinutes ?? global.CooldownMinutes,
			Events = perBot.Events ?? global.Events,
			Gotify = perBot.Gotify ?? global.Gotify,
			Ntfy = perBot.Ntfy ?? global.Ntfy,
			Templates = perBot.Templates ?? global.Templates
		};
	}

	internal bool TryGetTemplate(EEventType type, out string? template) {
		if (Templates != null) {
			return Templates.TryGetValue(type.ToString(), out template);
		}

		template = null;

		return false;
	}
}
