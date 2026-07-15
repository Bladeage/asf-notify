using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace ASFNotify.Steam;

// Best-effort resolution of package IDs to Steam game names via PICS. Returns an empty list on any failure,
// which the caller falls back from to plain package IDs. Only apps of type "game" are reported, to keep
// DLC/config/demo entries out of the notification.
internal static class GameNameResolver {
	internal static async Task<IReadOnlyList<string>> ResolveAsync(Bot bot, IReadOnlyCollection<uint> packageIDs) {
		try {
			HashSet<uint> appIDs = await GetAppIDsAsync(bot, packageIDs).ConfigureAwait(false);

			return appIDs.Count > 0 ? await GetGameNamesAsync(bot, appIDs).ConfigureAwait(false) : [];
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			return [];
		}
	}

	private static async Task<HashSet<uint>> GetAppIDsAsync(Bot bot, IReadOnlyCollection<uint> packageIDs) {
		HashSet<uint> appIDs = [];

		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await bot.SteamApps.PICSGetProductInfo([], packageIDs.Select(static id => new SteamApps.PICSRequest(id))).ToTask().ConfigureAwait(false);

		if (result.Results == null) {
			return appIDs;
		}

		foreach (SteamApps.PICSProductInfoCallback.PICSProductInfo package in result.Results.SelectMany(static callback => callback.Packages).Where(static entry => entry.Key != 0).Select(static entry => entry.Value)) {
			KeyValue appIDsKv = package.KeyValues["appids"];

			if (appIDsKv == KeyValue.Invalid) {
				continue;
			}

			foreach (KeyValue child in appIDsKv.Children) {
				if (uint.TryParse(child.Value, out uint appID) && (appID != 0)) {
					appIDs.Add(appID);
				}
			}
		}

		return appIDs;
	}

	private static async Task<IReadOnlyList<string>> GetGameNamesAsync(Bot bot, HashSet<uint> appIDs) {
		List<string> names = [];

		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await bot.SteamApps.PICSGetProductInfo(appIDs.Select(static id => new SteamApps.PICSRequest(id)), []).ToTask().ConfigureAwait(false);

		if (result.Results == null) {
			return names;
		}

		foreach (Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> apps in result.Results.Select(static callback => callback.Apps)) {
			foreach (uint appID in appIDs) {
				if (!apps.TryGetValue(appID, out SteamApps.PICSProductInfoCallback.PICSProductInfo? app)) {
					continue;
				}

				string? type = app.KeyValues["common"]["type"].AsString();

				if (string.IsNullOrEmpty(type) || !type.Equals("game", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}

				string? name = app.KeyValues["common"]["name"].AsString();

				if (!string.IsNullOrEmpty(name) && !names.Contains(name)) {
					names.Add(name);
				}
			}
		}

		return names;
	}
}
