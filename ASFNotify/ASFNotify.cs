using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Data;
using ArchiSteamFarm.Steam.Exchange;
using ArchiSteamFarm.Steam.Integration.Callbacks;
using ASFNotify.Configuration;
using ASFNotify.Data;
using ASFNotify.Notifiers;
using ASFNotify.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace ASFNotify;

#pragma warning disable CA1812 // ASF instantiates this class via reflection
[UsedImplicitly]
internal sealed class ASFNotify : IASF, IBot, IBotConnection, IBotCardsFarmerInfo, IBotModules, IBotCommand2, IBotSteamClient, IBotTradeOffer2, IBotTradeOfferResults, IBotUserNotifications, IUpdateAware, IGitHubPluginUpdates {
	private const string ConfigKey = nameof(ASFNotify);
	private const string ServerLabel = "ASF";
	private const int StartupGraceSeconds = 60;
	private const int GlobalSendTimeoutSeconds = 10;
	private const int MaxRedeemBatch = 25;

	private static readonly ImmutableArray<INotifier> Notifiers = [new NtfyNotifier(), new GotifyNotifier(), new AppriseNotifier()];

	// Disconnect reasons that mean a bot needs a human (bad credentials, 2FA, bans).
	private static readonly HashSet<EResult> AuthFailureResults = [
		EResult.InvalidPassword,
		EResult.AccountLogonDenied,
		EResult.AccountLoginDeniedNeedTwoFactor,
		EResult.TwoFactorCodeMismatch,
		EResult.AccountDisabled,
		EResult.Banned,
		EResult.Suspended,
		EResult.Revoked,
		EResult.Expired,
		EResult.AccessDenied
	];

	private readonly ConcurrentDictionary<string, PluginConfig> BotConfigs = new(StringComparer.OrdinalIgnoreCase);

	// Per-bot set of owned package IDs, to detect newly added licenses (GameRedeemed).
	private readonly ConcurrentDictionary<string, ImmutableHashSet<uint>> KnownPackages = new(StringComparer.OrdinalIgnoreCase);

	// Local calendar day on which a bot last reported a Steam login throttle, to keep it to one a day.
	private readonly ConcurrentDictionary<string, DateOnly> RateLimitReportedOn = new(StringComparer.OrdinalIgnoreCase);

	// Used to ignore the OnBotInit burst that fires for every existing bot at startup.
	private readonly DateTime StartupUtc = DateTime.UtcNow;

	public string Name => nameof(ASFNotify);

	public string RepositoryName => "Bladeage/asf-notify";

	public Version Version => typeof(ASFNotify).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	private NotificationDispatcher? Dispatcher;
	private PluginConfig? GlobalConfig;

	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		GlobalConfig = ParseConfig(additionalConfigProperties);
		Dispatcher = new NotificationDispatcher(Notifiers);

		if (GlobalConfig is { HasAnyBackend: true }) {
			ASF.ArchiLogger.LogGenericInfo($"[ASFNotify] Active backends: {DescribeBackends(GlobalConfig)}. Reported events: {string.Join(", ", GlobalConfig.EffectiveEvents)}.");

			if (GlobalConfig.EffectiveEvents.Contains(EEventType.AsfStarted)) {
				// WebBrowser is already up by the time OnASFInit runs, so queue it like any other event.
				string? asfVersion = typeof(ASF).Assembly.GetName().Version?.ToString();
				string message = string.IsNullOrEmpty(asfVersion) ? "ArchiSteamFarm started." : $"ArchiSteamFarm v{asfVersion} started.";
				Dispatcher.Enqueue(new NotificationEvent(ServerLabel, EEventType.AsfStarted, "🟢 ASF started", message, ENotificationPriority.Normal), GlobalConfig);
			}
		} else {
			ASF.ArchiLogger.LogGenericInfo("[ASFNotify] No global backend configured. Add the \"ASFNotify\" key to ASF.json (or a bot's config), then reload the config.");
		}

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"[ASFNotify] v{Version} loaded.");

		return Task.CompletedTask;
	}

	public Task OnBotInit(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (!IsWithinStartupGrace()) {
			Report(bot, EEventType.BotAdded, ENotificationPriority.Low, $"Bot {bot.BotName} was added.");
		}

		return Task.CompletedTask;
	}

	public Task OnBotDestroy(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		// Only fires on config deletion/rename, not on shutdown - so this is a real removal.
		if (!IsWithinStartupGrace()) {
			Report(bot, EEventType.BotRemoved, ENotificationPriority.Low, $"Bot {bot.BotName} was removed (its config was deleted or renamed).");
		}

		BotConfigs.TryRemove(bot.BotName, out _);
		KnownPackages.TryRemove(bot.BotName, out _);

		return Task.CompletedTask;
	}

	public Task OnBotInitModules(Bot bot, IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		ArgumentNullException.ThrowIfNull(bot);

		PluginConfig? config = ParseConfig(additionalConfigProperties);

		if (config != null) {
			BotConfigs[bot.BotName] = config;
		} else {
			BotConfigs.TryRemove(bot.BotName, out _);
		}

		return Task.CompletedTask;
	}

	public Task OnBotLoggedOn(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Report(bot, EEventType.LoggedOn, ENotificationPriority.Low, $"Bot {bot.BotName} successfully logged on to Steam.");

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		ArgumentNullException.ThrowIfNull(bot);

		// EResult.OK means ASF disconnected on purpose (shutdown, command, reconnect).
		if (reason == EResult.OK) {
			return Task.CompletedTask;
		}

		// Steam throttles logins for hours at a time while ASF quietly retries every few minutes, so one
		// incident would otherwise report all day. Nothing here needs a human, it just needs to be known.
		if (reason == EResult.RateLimitExceeded) {
			ReportRateLimited(bot, reason);

			return Task.CompletedTask;
		}

		// For auth failures prefer the higher-signal LoginAttention event, and skip the plain Disconnected
		// so one incident isn't reported twice.
		if (AuthFailureResults.Contains(reason) && Report(bot, EEventType.LoginAttention, ENotificationPriority.High, $"Bot {bot.BotName} needs attention — Steam login/auth failed. Reason: {reason}.", reason: reason.ToString())) {
			return Task.CompletedTask;
		}

		Report(bot, EEventType.Disconnected, ENotificationPriority.High, $"Bot {bot.BotName} was disconnected from Steam. Reason: {reason}.", reason: reason.ToString());

		return Task.CompletedTask;
	}

	public Task OnBotFarmingStarted(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Report(bot, EEventType.FarmingStarted, ENotificationPriority.Low, $"Bot {bot.BotName} started farming cards.");

		return Task.CompletedTask;
	}

	public Task OnBotFarmingFinished(Bot bot, bool farmedSomething) {
		ArgumentNullException.ThrowIfNull(bot);

		string message = farmedSomething ? $"Bot {bot.BotName} has finished farming cards." : $"Bot {bot.BotName} has nothing left to farm.";

		Report(bot, EEventType.FarmingFinished, ENotificationPriority.Normal, message, farmedSomething: farmedSomething);

		return Task.CompletedTask;
	}

	public Task OnBotFarmingStopped(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		Report(bot, EEventType.FarmingStopped, ENotificationPriority.Low, $"Card farming for bot {bot.BotName} was stopped.");

		return Task.CompletedTask;
	}

	public Task<bool> OnBotTradeOffer(Bot bot, TradeOffer tradeOffer, ParseTradeResult.EResult asfResult) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(tradeOffer);

		int give = tradeOffer.ItemsToGiveReadOnly.Count;
		int receive = tradeOffer.ItemsToReceiveReadOnly.Count;

		Report(bot, EEventType.TradeOffer, ENotificationPriority.Normal, $"Bot {bot.BotName} received a trade offer from {tradeOffer.OtherSteamID64} — giving {give}, receiving {receive}. ASF decision: {asfResult}.");

		// Returning true here would make ASF ACCEPT the offer; we only observe.
		return Task.FromResult(false);
	}

	public Task OnBotTradeOfferResults(Bot bot, IReadOnlyCollection<ParseTradeResult> tradeResults) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(tradeResults);

		int accepted = tradeResults.Count(static result => result.Result == ParseTradeResult.EResult.Accepted);
		int refused = tradeResults.Count(static result => result.Result is ParseTradeResult.EResult.Rejected or ParseTradeResult.EResult.Blacklisted or ParseTradeResult.EResult.Ignored);

		if (accepted > 0) {
			Report(bot, EEventType.TradeAccepted, ENotificationPriority.Normal, $"Bot {bot.BotName} accepted {accepted} trade offer(s).");
		}

		if (refused > 0) {
			Report(bot, EEventType.TradeRefused, ENotificationPriority.Low, $"Bot {bot.BotName} refused {refused} trade offer(s).");
		}

		return Task.CompletedTask;
	}

	public Task OnBotUserNotifications(Bot bot, IReadOnlyCollection<UserNotificationsCallback.EUserNotification> newNotifications) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(newNotifications);

		if (newNotifications.Contains(UserNotificationsCallback.EUserNotification.Gifts)) {
			Report(bot, EEventType.GiftReceived, ENotificationPriority.Normal, $"Bot {bot.BotName} received a Steam gift.");
		}

		if (newNotifications.Contains(UserNotificationsCallback.EUserNotification.AccountAlerts)) {
			Report(bot, EEventType.AccountAlert, ENotificationPriority.High, $"Steam raised an account alert for bot {bot.BotName}.");
		}

		return Task.CompletedTask;
	}

	public Task<IReadOnlyCollection<ClientMsgHandler>?> OnBotSteamHandlersInit(Bot bot) => Task.FromResult<IReadOnlyCollection<ClientMsgHandler>?>(null);

	public Task OnBotSteamCallbacksInit(Bot bot, CallbackManager callbackManager) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(callbackManager);

		callbackManager.Subscribe<SteamApps.LicenseListCallback>(callback => OnLicenseList(bot, callback));

		return Task.CompletedTask;
	}

	private void OnLicenseList(Bot bot, SteamApps.LicenseListCallback callback) {
		// Ignore empty/truncated or non-OK callbacks; Steam sends these in the wild and they must not
		// disturb the baseline.
		if ((callback.Result != EResult.OK) || callback.LicenseList is not { Count: > 0 }) {
			return;
		}

		Dictionary<uint, EPaymentMethod> current = new();

		foreach (SteamApps.LicenseListCallback.License license in callback.LicenseList) {
			if (license.LicenseFlags.HasFlag(ELicenseFlags.Borrowed)) {
				continue;
			}

			current[license.PackageID] = license.PaymentMethod;
		}

		ImmutableHashSet<uint> currentSet = [.. current.Keys];

		// Always maintain the baseline (cheap, no PICS), even when GameRedeemed is disabled, so re-enabling
		// it later doesn't replay everything acquired meanwhile. Union rather than replace, so a transiently
		// truncated list can't drop packages and later make them look newly redeemed.
		bool hadBaseline = KnownPackages.TryGetValue(bot.BotName, out ImmutableHashSet<uint>? known);
		KnownPackages[bot.BotName] = hadBaseline ? known!.Union(currentSet) : currentSet;

		// The first callback (initial license list at login) is only the baseline.
		if (!hadBaseline) {
			// One-time diagnostic (visible at ASF's Debug log level): payment-method breakdown of the account.
			ASF.ArchiLogger.LogGenericDebug($"[ASFNotify] {bot.BotName}: license payment methods — {string.Join(", ", current.Values.GroupBy(static method => method).Select(static group => $"{group.Key}={group.Count()}"))}");

			return;
		}

		// Only diff and notify when the event is enabled for this bot and a backend is configured.
		PluginConfig? config = ResolveConfig(bot);

		if (config is not { HasAnyBackend: true } || !config.EffectiveEvents.Contains(EEventType.GameRedeemed)) {
			return;
		}

		// Packages newly added since the baseline.
		List<uint> newPackages = current.Keys.Where(id => !known!.Contains(id)).ToList();

		if (newPackages.Count == 0) {
			return;
		}

		// Diagnostic (Debug level): payment-method breakdown of the newly added packages.
		ASF.ArchiLogger.LogGenericDebug($"[ASFNotify] {bot.BotName}: {newPackages.Count} new package(s) — {string.Join(", ", newPackages.GroupBy(id => current[id]).Select(group => $"{group.Key}={group.Count()}"))}");

		// Only genuine key redemptions — background redeeming and keys forwarded between bots — which arrive
		// as ActivationCode. Free-package grants and Steam gifts both arrive as Complimentary and are excluded
		// here (they're indistinguishable, and real gifts are covered by the separate GiftReceived event).
		List<uint> redeemed = newPackages.Where(id => current[id] == EPaymentMethod.ActivationCode).ToList();

		if (redeemed.Count > 0) {
			_ = ReportRedeemedAsync(bot, redeemed);
		}
	}

	private async Task ReportRedeemedAsync(Bot bot, List<uint> packageIDs) {
		try {
			// A very large batch is aggregated without per-title PICS lookups (avoids a big lookup burst
			// and an unwieldy message) while still telling the user something happened.
			if (packageIDs.Count > MaxRedeemBatch) {
				Report(bot, EEventType.GameRedeemed, ENotificationPriority.Normal, $"Bot {bot.BotName} redeemed {packageIDs.Count} new licenses.");

				return;
			}

			IReadOnlyList<string> names = await GameNameResolver.ResolveAsync(bot, packageIDs).ConfigureAwait(false);

			string what = names.Count > 0
				? string.Join(", ", names)
				: packageIDs.Count == 1 ? $"a new license (package {packageIDs[0]})" : $"{packageIDs.Count} new licenses ({string.Join(", ", packageIDs)})";

			Report(bot, EEventType.GameRedeemed, ENotificationPriority.Normal, $"Bot {bot.BotName} redeemed {what}.");
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);
		}
	}

	public Task OnUpdateProceeding(Version currentVersion, Version newVersion) => Task.CompletedTask;

	public Task OnUpdateFinished(Version currentVersion, Version newVersion) {
		ArgumentNullException.ThrowIfNull(currentVersion);
		ArgumentNullException.ThrowIfNull(newVersion);

		return SendGlobalAsync(EEventType.AsfUpdated, ENotificationPriority.High, "🆙 ASF updated", $"ArchiSteamFarm updated from {currentVersion} to {newVersion}. Restarting…");
	}

	public Task OnPluginUpdateProceeding() => Task.CompletedTask;

	public Task OnPluginUpdateFinished() => SendGlobalAsync(EEventType.PluginUpdated, ENotificationPriority.Normal, "🆙 ASFNotify updated", "ASFNotify was updated. Restarting to apply the new version.");

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(args);

		if (args.Length == 0) {
			return null;
		}

		switch (args[0].ToUpperInvariant()) {
			case "NOTIFYTEST" when access >= EAccess.Master:
				return await SendTestAsync(bot).ConfigureAwait(false);
			default:
				return null;
		}
	}

	// At most one report per bot per local day, so a bot stuck behind Steam's login throttle stays a single
	// notification instead of one every retry. The next day is allowed to report again.
	private void ReportRateLimited(Bot bot, EResult reason) {
		DateOnly today = DateOnly.FromDateTime(DateTime.Now);

		while (true) {
			if (RateLimitReportedOn.TryGetValue(bot.BotName, out DateOnly last)) {
				if (last == today) {
					return;
				}

				if (!RateLimitReportedOn.TryUpdate(bot.BotName, today, last)) {
					continue;
				}
			} else if (!RateLimitReportedOn.TryAdd(bot.BotName, today)) {
				continue;
			}

			break;
		}

		string message = $"Bot {bot.BotName} cannot log in, Steam is rate limiting it. ASF keeps retrying on its own, and this is reported once a day.";

		// Fall back to Disconnected for setups that only enabled that one.
		if (!Report(bot, EEventType.LoginAttention, ENotificationPriority.High, message, reason: reason.ToString())) {
			Report(bot, EEventType.Disconnected, ENotificationPriority.High, message, reason: reason.ToString());
		}
	}

	// Returns true if this event is configured for delivery (a backend is set and the event is enabled).
	// The notification may still be cooldown-suppressed or dropped downstream.
	private bool Report(Bot bot, EEventType type, ENotificationPriority priority, string defaultMessage, string? reason = null, bool? farmedSomething = null) {
		NotificationDispatcher? dispatcher = Dispatcher;

		if (dispatcher == null) {
			return false;
		}

		PluginConfig? config = ResolveConfig(bot);

		if (config is not { HasAnyBackend: true } || !config.EffectiveEvents.Contains(type)) {
			return false;
		}

		string message = defaultMessage;

		if (config.TryGetTemplate(type, out string? template) && !string.IsNullOrEmpty(template)) {
			message = ApplyPlaceholders(template, bot, reason, farmedSomething);
		}

		NotificationEvent notification = new(bot.BotName, type, BuildTitle(type, bot.BotName), message, priority);
		dispatcher.Enqueue(notification, config);

		return true;
	}

	// Delivery for events that carry no Bot (ASF/plugin updates). Uses the global config and sends
	// synchronously, because these fire right before ASF restarts and a queued push might not flush.
	private async Task SendGlobalAsync(EEventType type, ENotificationPriority priority, string title, string message) {
		PluginConfig? config = GlobalConfig;

		if (config is not { HasAnyBackend: true } || !config.EffectiveEvents.Contains(type)) {
			return;
		}

		NotificationEvent notification = new(ServerLabel, type, title, message, priority);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(GlobalSendTimeoutSeconds));

		foreach (INotifier notifier in Notifiers) {
			if (!notifier.IsConfigured(config)) {
				continue;
			}

			try {
				await notifier.SendAsync(config, notification, cts.Token).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericWarningException(e);
			}
		}
	}

	private async Task<string> SendTestAsync(Bot bot) {
		PluginConfig? config = ResolveConfig(bot);

		if (config is not { HasAnyBackend: true }) {
			return $"[ASFNotify] No backend configured for {bot.BotName}.";
		}

		NotificationEvent test = new(bot.BotName, EEventType.LoggedOn, $"🔔 ASFNotify test ({bot.BotName})", $"This is a test notification from ASFNotify for bot {bot.BotName}.", ENotificationPriority.Normal);

		List<string> results = [];

		foreach (INotifier notifier in Notifiers) {
			if (!notifier.IsConfigured(config)) {
				continue;
			}

			bool ok;

			try {
				ok = await notifier.SendAsync(config, test).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericWarningException(e);
				ok = false;
			}

			results.Add($"{notifier.Name}: {(ok ? "OK" : "FAILED")}");
		}

		return "[ASFNotify] Test → " + string.Join(" | ", results);
	}

	private bool IsWithinStartupGrace() => DateTime.UtcNow - StartupUtc < TimeSpan.FromSeconds(StartupGraceSeconds);

	private PluginConfig? ResolveConfig(Bot bot) => PluginConfig.Merge(GlobalConfig, BotConfigs.GetValueOrDefault(bot.BotName));

	private static PluginConfig? ParseConfig(IReadOnlyDictionary<string, JsonElement>? properties) {
		if ((properties == null) || !properties.TryGetValue(ConfigKey, out JsonElement element) || (element.ValueKind != JsonValueKind.Object)) {
			return null;
		}

		try {
			return PluginConfig.FromJson(element);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);

			return null;
		}
	}

	private static string BuildTitle(EEventType type, string botName) => type switch {
		EEventType.LoggedOn => $"✅ {botName} online",
		EEventType.LoginAttention => $"🔐 {botName} needs attention",
		EEventType.Disconnected => $"⚠️ {botName} disconnected",
		EEventType.FarmingStarted => $"▶️ {botName} farming started",
		EEventType.FarmingFinished => $"🎉 {botName} finished farming",
		EEventType.FarmingStopped => $"⏹️ {botName} farming stopped",
		EEventType.TradeOffer => $"🤝 {botName} trade offer",
		EEventType.TradeAccepted => $"✅ {botName} trade accepted",
		EEventType.TradeRefused => $"🚫 {botName} trade refused",
		EEventType.GiftReceived => $"🎁 {botName} gift received",
		EEventType.AccountAlert => $"🔔 {botName} account alert",
		EEventType.GameRedeemed => $"🎮 {botName} new game",
		EEventType.BotAdded => $"➕ {botName} added",
		EEventType.BotRemoved => $"➖ {botName} removed",
		_ => $"🔔 {botName}"
	};

	private static string ApplyPlaceholders(string template, Bot bot, string? reason, bool? farmedSomething) =>
		template
			.Replace("{Bot}", bot.BotName, StringComparison.OrdinalIgnoreCase)
			.Replace("{SteamID}", bot.SteamID.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
			.Replace("{Reason}", reason ?? string.Empty, StringComparison.OrdinalIgnoreCase)
			.Replace("{FarmedSomething}", farmedSomething?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

	private static string DescribeBackends(PluginConfig config) {
		List<string> backends = [];

		if (config.Ntfy?.IsValid == true) {
			backends.Add("ntfy");
		}

		if (config.Gotify?.IsValid == true) {
			backends.Add("Gotify");
		}

		if (config.Apprise?.IsValid == true) {
			backends.Add("Apprise");
		}

		return backends.Count > 0 ? string.Join(", ", backends) : "none";
	}
}
#pragma warning restore CA1812 // ASF instantiates this class via reflection
