using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using SteamKit2;

namespace ASFNotify.Steam;

// Best-effort resolution of package IDs to Steam app names via PICS. Returns an empty list on any
// failure, from which the caller falls back to plain package IDs. Access tokens are fetched first so
// token-gated (often newly released) titles still resolve; "game" apps are preferred, but any app name
// beats a raw package ID, so DLC/software/soundtrack packages still get a name.
internal static class GameNameResolver {
	internal static async Task<IReadOnlyList<string>> ResolveAsync(Bot bot, IReadOnlyCollection<uint> packageIDs) {
		try {
			HashSet<uint> appIDs = await GetAppIDsAsync(bot, packageIDs).ConfigureAwait(false);

			return appIDs.Count > 0 ? await GetNamesAsync(bot, appIDs).ConfigureAwait(false) : [];
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			return [];
		}
	}

	private static async Task<HashSet<uint>> GetAppIDsAsync(Bot bot, IReadOnlyCollection<uint> packageIDs) {
		HashSet<uint> appIDs = [];

		SteamApps.PICSTokensCallback tokens = await bot.SteamApps.PICSGetAccessTokens([], packageIDs).ToTask().ConfigureAwait(false);

		IEnumerable<SteamApps.PICSRequest> requests = packageIDs.Select(id => new SteamApps.PICSRequest(id, tokens.PackageTokens.GetValueOrDefault(id)));

		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await bot.SteamApps.PICSGetProductInfo([], requests).ToTask().ConfigureAwait(false);

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

	private static async Task<IReadOnlyList<string>> GetNamesAsync(Bot bot, HashSet<uint> appIDs) {
		SteamApps.PICSTokensCallback tokens = await bot.SteamApps.PICSGetAccessTokens(appIDs, []).ToTask().ConfigureAwait(false);

		IEnumerable<SteamApps.PICSRequest> requests = appIDs.Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.GetValueOrDefault(id)));

		AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet result = await bot.SteamApps.PICSGetProductInfo(requests, []).ToTask().ConfigureAwait(false);

		List<string> gameNames = [];
		List<string> otherNames = [];

		if (result.Results == null) {
			return gameNames;
		}

		foreach (Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> apps in result.Results.Select(static callback => callback.Apps)) {
			foreach (uint appID in appIDs) {
				if (!apps.TryGetValue(appID, out SteamApps.PICSProductInfoCallback.PICSProductInfo? app)) {
					continue;
				}

				string? name = app.KeyValues["common"]["name"].AsString();

				if (string.IsNullOrEmpty(name)) {
					continue;
				}

				string? type = app.KeyValues["common"]["type"].AsString();

				if (!string.IsNullOrEmpty(type) && type.Equals("game", StringComparison.OrdinalIgnoreCase)) {
					if (!gameNames.Contains(name)) {
						gameNames.Add(name);
					}
				} else if (!otherNames.Contains(name)) {
					otherNames.Add(name);
				}
			}
		}

		// Prefer game titles; if the redeemed packages were DLC/software only, still report their names.
		return gameNames.Count > 0 ? gameNames : otherNames;
	}
}
