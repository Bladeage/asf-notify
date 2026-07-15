using System;
using System.Text.Json;

namespace ASFNotify.Configuration;

// Config is read straight off the JSON DOM (not JsonSerializer) because ASF runs trimmed
// with reflection-based serialization disabled.
internal static class JsonElementExtensions {
	internal static bool TryGetPropertyCI(this JsonElement element, string name, out JsonElement value) {
		if (element.ValueKind == JsonValueKind.Object) {
			foreach (JsonProperty property in element.EnumerateObject()) {
				if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
					value = property.Value;

					return true;
				}
			}
		}

		value = default;

		return false;
	}

	internal static string? GetStringOrNull(this JsonElement element, string name) => element.TryGetPropertyCI(name, out JsonElement value) && (value.ValueKind == JsonValueKind.String) ? value.GetString() : null;

	internal static Uri? GetUriOrNull(this JsonElement element, string name) {
		string? raw = element.GetStringOrNull(name);

		return !string.IsNullOrEmpty(raw) && Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ? uri : null;
	}

	internal static byte? GetByteOrNull(this JsonElement element, string name) => element.TryGetPropertyCI(name, out JsonElement value) && (value.ValueKind == JsonValueKind.Number) && value.TryGetByte(out byte result) ? result : null;
}
