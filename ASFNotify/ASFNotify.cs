using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

	// A disconnect only becomes a push if the bot is still offline after this; routine blips (daily
	// Steam disconnect, maintenance flaps) heal well within it.
	private const int DisconnectDebounceSeconds = 120;

	// Stopped fires right before Finished and around internal stop+start re-batches; held briefly so
	// only real interruptions are reported.
	private const int FarmingStoppedHoldSeconds = 5;

	// How many transient auth failures in a row (no successful logon in between) earn a LoginAttention.
	private const byte TransientAuthStrikeThreshold = 2;

	private static readonly ImmutableArray<INotifier> Notifiers = [new NtfyNotifier(), new GotifyNotifier(), new AppriseNotifier()];

	// Disconnect reasons that always mean a human is needed. Account states are never transient, and
	// the two guard-denied results mean Steam wants interactive 2FA/email input - a headless ASF stops
	// the bot after the first one, so waiting for a second strike would swallow the alert entirely.
	private static readonly HashSet<EResult> HardAuthFailureResults = [
		EResult.AccountDisabled,
		EResult.Banned,
		EResult.Suspended,
		EResult.Revoked,
		EResult.Expired,
		EResult.AccountLogonDenied,
		EResult.AccountLoginDeniedNeedTwoFactor
	];

	// Auth failures Steam also returns during hiccups it recovers from on its own (a transient
	// InvalidPassword/AccessDenied would otherwise cry wolf); these need TransientAuthStrikeThreshold
	// strikes in a row to report. ASF retries these (and gives up after 3 in a row), so a genuine
	// failure reliably reaches the second strike.
	private static readonly HashSet<EResult> TransientAuthFailureResults = [
		EResult.InvalidPassword,
		EResult.TwoFactorCodeMismatch,
		EResult.AccessDenied
	];

	// The per-type notification counts on UserNotificationsCallback are internal; reading them via
	// reflection is what makes gift/alert deduplication possible. The member is used by ASF itself so
	// it survives trimming; if it ever disappears, OnBotUserNotifications takes over as fallback.
	private static readonly FieldInfo? NotificationCountsField = typeof(UserNotificationsCallback).GetField("Notifications", BindingFlags.Instance | BindingFlags.NonPublic);

	private static bool NotificationCountsBroken;

	private static bool UserNotificationCountsUsable => (NotificationCountsField != null) && !NotificationCountsBroken;

	private readonly ConcurrentDictionary<string, PluginConfig> BotConfigs = new(StringComparer.OrdinalIgnoreCase);

	// Per-bot set of owned package IDs, to detect newly added licenses (GameRedeemed).
	private readonly ConcurrentDictionary<string, ImmutableHashSet<uint>> KnownPackages = new(StringComparer.OrdinalIgnoreCase);

	// Local calendar day on which a bot last reported a Steam login throttle, to keep it to one a day.
	private readonly ConcurrentDictionary<string, DateOnly> RateLimitReportedOn = new(StringComparer.OrdinalIgnoreCase);

	// Last seen pending-count per bot for gifts and account alerts; only an increase reports, so the
	// same unclaimed item isn't re-announced on every reconnect (ASF forgets its own counts there).
	private readonly ConcurrentDictionary<string, uint> GiftCounts = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, uint> AlertCounts = new(StringComparer.OrdinalIgnoreCase);

	// Consecutive transient auth failures per bot, reset by a successful logon.
	private readonly ConcurrentDictionary<string, byte> TransientAuthStrikes = new(StringComparer.OrdinalIgnoreCase);

	// Pending trade offers already announced, per bot. ASF forgets which offers it parsed on every
	// disconnect and re-runs the hook for the same still-pending offer at each login; announcing each
	// offer ID once keeps that from becoming a daily repeat.
	private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, bool>> AnnouncedPendingTrades = new(StringComparer.OrdinalIgnoreCase);

	// Bots for which an incident (Disconnected/LoginAttention) was actually pushed; the next successful
	// logon closes the loop with a LoggedOn recovery notice.
	private readonly ConcurrentDictionary<string, bool> IncidentReported = new(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, CancellationTokenSource> PendingDisconnects = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, CancellationTokenSource> PendingFarmingStops = new(StringComparer.OrdinalIgnoreCase);

	private readonly ConcurrentDictionary<string, FarmingTracker> FarmingTrackers = new(StringComparer.OrdinalIgnoreCase);

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
		RateLimitReportedOn.TryRemove(bot.BotName, out _);
		GiftCounts.TryRemove(bot.BotName, out _);
		AlertCounts.TryRemove(bot.BotName, out _);
		TransientAuthStrikes.TryRemove(bot.BotName, out _);
		IncidentReported.TryRemove(bot.BotName, out _);
		AnnouncedPendingTrades.TryRemove(bot.BotName, out _);

		CancelPendingDisconnect(bot);
		CancelPendingFarmingStopped(bot);

		if (FarmingTrackers.TryRemove(bot.BotName, out FarmingTracker? tracker)) {
			tracker.Dispose();
		}

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

		CancelPendingDisconnect(bot);

		TransientAuthStrikes.TryRemove(bot.BotName, out _);

		// LoggedOn is a recovery notice: it only fires after an incident was actually pushed for this
		// bot, closing the loop instead of narrating every routine (re)connect.
		if (IncidentReported.TryRemove(bot.BotName, out _)) {
			Report(bot, EEventType.LoggedOn, ENotificationPriority.Low, $"Bot {bot.BotName} is back online.");
		}

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
		// AccountLoginDeniedThrottle is ASF's other rate-limiting result, treated identically.
		if (reason is EResult.RateLimitExceeded or EResult.AccountLoginDeniedThrottle) {
			ReportRateLimited(bot, reason);

			return Task.CompletedTask;
		}

		// Hard account states are worth an immediate push; transient auth failures need a second strike
		// in a row, because Steam returns them for outages ASF recovers from by itself.
		if (HardAuthFailureResults.Contains(reason)) {
			ReportLoginAttention(bot, reason);

			return Task.CompletedTask;
		}

		if (TransientAuthFailureResults.Contains(reason)) {
			byte strikes = TransientAuthStrikes.AddOrUpdate(bot.BotName, 1, static (_, current) => (byte) Math.Min(byte.MaxValue, current + 1));

			// Exactly at the threshold: one push per incident, not one per retry. The first strike still
			// arms the offline debounce, so a bot that ASF stops after a single failure (e.g. an expired
			// refresh token with no password to retry with) surfaces as Disconnected instead of nothing.
			if (strikes == TransientAuthStrikeThreshold) {
				ReportLoginAttention(bot, reason);
			} else if (strikes < TransientAuthStrikeThreshold) {
				ScheduleDisconnectedReport(bot, reason);
			}

			return Task.CompletedTask;
		}

		// Anything else is connectivity noise that usually heals in seconds; only a bot that is still
		// offline after the debounce is worth a push.
		ScheduleDisconnectedReport(bot, reason);

		return Task.CompletedTask;
	}

	// Prefer the higher-signal LoginAttention event, fall back to Disconnected for setups that only
	// enabled that one; either way a single incident is reported once, and a pending offline debounce
	// for the same incident is dropped.
	private void ReportLoginAttention(Bot bot, EResult reason) {
		bool reported = Report(bot, EEventType.LoginAttention, ENotificationPriority.High, $"Bot {bot.BotName} needs attention — Steam login/auth failed. Reason: {reason}.", reason: reason.ToString())
			|| Report(bot, EEventType.Disconnected, ENotificationPriority.High, $"Bot {bot.BotName} was disconnected from Steam. Reason: {reason}.", reason: reason.ToString());

		if (reported) {
			IncidentReported[bot.BotName] = true;
			CancelPendingDisconnect(bot);
		}
	}

	private void ScheduleDisconnectedReport(Bot bot, EResult reason) {
		// One push per outage: while an incident for this bot is already out (and no successful logon
		// cleared it yet), ASF's continuing reconnect attempts don't get to re-report every few minutes.
		if (IncidentReported.ContainsKey(bot.BotName)) {
			return;
		}

		CancellationTokenSource cts = new();

		// The first disconnect starts the clock; later ones inside the window don't extend it.
		if (!PendingDisconnects.TryAdd(bot.BotName, cts)) {
			cts.Dispose();

			return;
		}

		_ = Task.Run(async () => {
			try {
				await Task.Delay(TimeSpan.FromSeconds(DisconnectDebounceSeconds), cts.Token).ConfigureAwait(false);

				// Still wanted online, still not online: a real outage rather than a blip.
				if (bot.KeepRunning && !bot.IsConnectedAndLoggedOn && Report(bot, EEventType.Disconnected, ENotificationPriority.High, $"Bot {bot.BotName} was disconnected from Steam and has not come back for {DisconnectDebounceSeconds / 60} minutes. Reason: {reason}.", reason: reason.ToString())) {
					IncidentReported[bot.BotName] = true;
				}
			} catch (OperationCanceledException) {
				// Reconnected in time; nothing to tell.
			} finally {
				PendingDisconnects.TryRemove(new KeyValuePair<string, CancellationTokenSource>(bot.BotName, cts));
				cts.Dispose();
			}
		});
	}

	public Task OnBotFarmingStarted(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		CancelPendingFarmingStopped(bot);

		// No push of its own: the tracker announces what is actually being farmed, per game or batch.
		// A tracker left over from a destroyed bot of the same name watches dead collections, so it is
		// replaced rather than reused.
		FarmingTracker tracker = FarmingTrackers.AddOrUpdate(
			bot.BotName,
			_ => new FarmingTracker(bot, this),
			(_, existing) => {
				if (existing.IsFor(bot)) {
					return existing;
				}

				existing.Dispose();

				return new FarmingTracker(bot, this);
			}
		);

		tracker.OnFarmingStarted();

		return Task.CompletedTask;
	}

	public Task OnBotFarmingFinished(Bot bot, bool farmedSomething) {
		ArgumentNullException.ThrowIfNull(bot);

		CancelPendingFarmingStopped(bot);

		(int games, long cards) = FarmingTrackers.TryGetValue(bot.BotName, out FarmingTracker? tracker) ? tracker.OnFarmingFinished() : (0, 0L);

		// ASF raises this with farmedSomething=false at every logon and idle recheck of an account that
		// has nothing to farm - no farming happened, so there is nothing to report.
		if (!farmedSomething) {
			return Task.CompletedTask;
		}

		string message = games > 0
			? $"Bot {bot.BotName} finished farming: {games} game(s), {cards} card(s) this session."
			: $"Bot {bot.BotName} has finished farming cards.";

		Report(bot, EEventType.FarmingFinished, ENotificationPriority.Normal, message, extras: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
			{ "Count", games.ToString(CultureInfo.InvariantCulture) },
			{ "Cards", cards.ToString(CultureInfo.InvariantCulture) },
			{ "FarmedSomething", "True" } // deprecated placeholder, kept so old templates don't break
		});

		return Task.CompletedTask;
	}

	public Task OnBotFarmingStopped(Bot bot) {
		ArgumentNullException.ThrowIfNull(bot);

		if (FarmingTrackers.TryGetValue(bot.BotName, out FarmingTracker? tracker)) {
			tracker.OnFarmingStopped();
		}

		ScheduleFarmingStoppedReport(bot);

		return Task.CompletedTask;
	}

	// Stopped fires as part of every farming teardown: right before Finished, on internal stop+start
	// re-batches, and on disconnects. Held briefly and only reported when none of those follow - i.e. a
	// pause, a stop command, or the account being used elsewhere.
	private void ScheduleFarmingStoppedReport(Bot bot) {
		CancellationTokenSource cts = new();

		if (!PendingFarmingStops.TryAdd(bot.BotName, cts)) {
			cts.Dispose();

			return;
		}

		_ = Task.Run(async () => {
			try {
				await Task.Delay(TimeSpan.FromSeconds(FarmingStoppedHoldSeconds), cts.Token).ConfigureAwait(false);

				// The hold alone isn't enough: ASF legitimately takes longer than the hold between Stopped
				// and the follow-up Started/Finished (badge re-scans, loot on finish). So only the states
				// that mean a genuine interruption report: paused, or the account is in use elsewhere. A
				// disconnect tells its own story via Disconnected, and teardown noise has neither marker.
				if (bot.IsConnectedAndLoggedOn && !bot.CardsFarmer.NowFarming && (bot.CardsFarmer.Paused || !bot.IsPlayingPossible)) {
					Report(bot, EEventType.FarmingStopped, ENotificationPriority.Low, $"Card farming for bot {bot.BotName} was stopped.");
				}
			} catch (OperationCanceledException) {
				// Farming finished or restarted right after; not an interruption worth telling.
			} finally {
				PendingFarmingStops.TryRemove(new KeyValuePair<string, CancellationTokenSource>(bot.BotName, cts));
				cts.Dispose();
			}
		});
	}

	private void CancelPendingFarmingStopped(Bot bot) {
		if (PendingFarmingStops.TryRemove(bot.BotName, out CancellationTokenSource? pending)) {
			using (pending) {
				CancelSafely(pending);
			}
		}
	}

	// The owning task disposes its CTS in a finally; a canceller can lose that race, so a cancel that
	// hits an already-disposed source is simply a job already done.
	private static void CancelSafely(CancellationTokenSource cts) {
		try {
			cts.Cancel();
		} catch (ObjectDisposedException) {
			// The pending task completed and cleaned up first.
		}
	}

	public Task<bool> OnBotTradeOffer(Bot bot, TradeOffer tradeOffer, ParseTradeResult.EResult asfResult) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(tradeOffer);

		// Offers ASF resolves on its own are covered by TradeAccepted/TradeRefused; the only offer that
		// needs a human is one ASF leaves pending (Ignored), and each one is announced exactly once.
		if (asfResult == ParseTradeResult.EResult.Ignored) {
			ConcurrentDictionary<ulong, bool> announced = AnnouncedPendingTrades.GetOrAdd(bot.BotName, static _ => new ConcurrentDictionary<ulong, bool>());

			if (announced.TryAdd(tradeOffer.TradeOfferID, true)) {
				int give = tradeOffer.ItemsToGiveReadOnly.Count;
				int receive = tradeOffer.ItemsToReceiveReadOnly.Count;

				Report(bot, EEventType.TradeOffer, ENotificationPriority.Normal, $"Bot {bot.BotName} has a pending trade offer from {tradeOffer.OtherSteamID64} (giving {give}, receiving {receive}) that ASF will not handle automatically.");
			}
		}

		// Returning true here would make ASF ACCEPT the offer; we only observe.
		return Task.FromResult(false);
	}

	public Task OnBotTradeOfferResults(Bot bot, IReadOnlyCollection<ParseTradeResult> tradeResults) {
		ArgumentNullException.ThrowIfNull(bot);
		ArgumentNullException.ThrowIfNull(tradeResults);

		// Ignored is deliberately not counted here: ASF leaves those offers pending, which is the new
		// TradeOffer event's story, not a refusal.
		int accepted = tradeResults.Count(static result => result.Result == ParseTradeResult.EResult.Accepted);
		int refused = tradeResults.Count(static result => result.Result is ParseTradeResult.EResult.Rejected or ParseTradeResult.EResult.Blacklisted);

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

		// ASF forgets its notification counts on every disconnect, so this hook re-announces the same
		// pending gift/alert at each login. The count diff in OnUserNotificationCounts is the preferred
		// path; this one only kicks in when the counts can't be read.
		if (UserNotificationCountsUsable) {
			return Task.CompletedTask;
		}

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
		callbackManager.Subscribe<UserNotificationsCallback>(callback => OnUserNotificationCounts(bot, callback));

		return Task.CompletedTask;
	}

	private void OnUserNotificationCounts(Bot bot, UserNotificationsCallback callback) {
		if (!UserNotificationCountsUsable) {
			return;
		}

		Dictionary<UserNotificationsCallback.EUserNotification, uint>? counts;

		try {
			counts = NotificationCountsField!.GetValue(callback) as Dictionary<UserNotificationsCallback.EUserNotification, uint>;
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericWarningException(e);

			counts = null;
		}

		if (counts == null) {
			NotificationCountsBroken = true;
			ASF.ArchiLogger.LogGenericWarning("[ASFNotify] Cannot read Steam notification counts; gift/alert notifications fall back to per-login reporting.");

			return;
		}

		ReportNotificationDelta(bot, UserNotificationsCallback.EUserNotification.Gifts, counts, GiftCounts, EEventType.GiftReceived, ENotificationPriority.Normal, static (botName, delta) => delta == 1 ? $"Bot {botName} received a Steam gift." : $"Bot {botName} received {delta} Steam gifts.");
		ReportNotificationDelta(bot, UserNotificationsCallback.EUserNotification.AccountAlerts, counts, AlertCounts, EEventType.AccountAlert, ENotificationPriority.High, static (botName, delta) => delta == 1 ? $"Steam raised an account alert for bot {botName}." : $"Steam raised {delta} account alerts for bot {botName}.");
	}

	// Reports only when the pending count RISES above the last seen value; a drop (claimed/read) just
	// lowers the baseline silently. The baseline is in-memory, so after an ASF restart a still-pending
	// item is re-announced exactly once - a reminder rather than a bug, and it also covers items that
	// arrived while ASF was down.
	private void ReportNotificationDelta(Bot bot, UserNotificationsCallback.EUserNotification type, Dictionary<UserNotificationsCallback.EUserNotification, uint> counts, ConcurrentDictionary<string, uint> baselines, EEventType eventType, ENotificationPriority priority, Func<string, uint, string> messageFactory) {
		// Some callback variants carry only a subset of types; an absent type means "no news", not zero.
		if (!counts.TryGetValue(type, out uint count)) {
			return;
		}

		uint baseline = baselines.GetValueOrDefault(bot.BotName);
		baselines[bot.BotName] = count;

		if (count > baseline) {
			Report(bot, eventType, priority, messageFactory(bot.BotName, count - baseline));
		}
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
	// notification instead of one every retry. The next day is allowed to report again. The day marker is
	// stamped only when a push was actually enqueued, so the day isn't consumed while neither event is
	// configured.
	private void ReportRateLimited(Bot bot, EResult reason) {
		DateOnly today = DateOnly.FromDateTime(DateTime.Now);

		if (RateLimitReportedOn.TryGetValue(bot.BotName, out DateOnly last) && (last == today)) {
			return;
		}

		string message = $"Bot {bot.BotName} cannot log in, Steam is rate limiting it. ASF keeps retrying on its own, and this is reported once a day.";

		// Fall back to Disconnected for setups that only enabled that one.
		if (Report(bot, EEventType.LoginAttention, ENotificationPriority.High, message, reason: reason.ToString()) || Report(bot, EEventType.Disconnected, ENotificationPriority.High, message, reason: reason.ToString())) {
			RateLimitReportedOn[bot.BotName] = today;
			IncidentReported[bot.BotName] = true;
			CancelPendingDisconnect(bot);
		}
	}

	private void CancelPendingDisconnect(Bot bot) {
		if (PendingDisconnects.TryRemove(bot.BotName, out CancellationTokenSource? pending)) {
			using (pending) {
				CancelSafely(pending);
			}
		}
	}

	// Returns true if this event is configured for delivery (a backend is set and the event is enabled).
	// The notification may still be cooldown-suppressed or dropped downstream.
	private bool Report(Bot bot, EEventType type, ENotificationPriority priority, string defaultMessage, string? title = null, string? reason = null, IReadOnlyDictionary<string, string>? extras = null, bool bypassCooldown = false) {
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
			message = ApplyPlaceholders(template, bot, reason, extras);
		}

		NotificationEvent notification = new(bot.BotName, type, title ?? BuildTitle(type, bot.BotName), message, priority, bypassCooldown);
		dispatcher.Enqueue(notification, config);

		return true;
	}

	// Entry point for the FarmingTracker, which builds titles with game context of its own. Its events
	// deduplicate themselves (once per game/batch per session, aggregated finishes), so the generic
	// cooldown - which would silently drop a second, different game - is bypassed.
	internal void ReportFarming(Bot bot, EEventType type, ENotificationPriority priority, string title, string defaultMessage, IReadOnlyDictionary<string, string>? extras) => Report(bot, type, priority, defaultMessage, title, extras: extras, bypassCooldown: true);

	internal byte GetCooldownMinutes(Bot bot) => ResolveConfig(bot)?.EffectiveCooldownMinutes ?? PluginConfig.DefaultCooldownMinutes;

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

	// Fallback titles; the FarmingTracker passes richer ones (with game names) where it has them.
	private static string BuildTitle(EEventType type, string botName) => type switch {
		EEventType.LoggedOn => $"✅ {botName} online",
		EEventType.LoginAttention => $"🔐 {botName} needs attention",
		EEventType.Disconnected => $"⚠️ {botName} disconnected",
		EEventType.GameFarmingStarted => $"▶️ {botName} farming",
		EEventType.GameFarmingFinished => $"🃏 {botName} cards done",
		EEventType.MassFarmingStarted => $"⏫ {botName} mass farming",
		EEventType.FarmingFinished => $"🎉 {botName} finished farming",
		EEventType.FarmingStopped => $"⏹️ {botName} farming stopped",
		EEventType.TradeOffer => $"🤝 {botName} trade offer needs review",
		EEventType.TradeAccepted => $"✅ {botName} trade accepted",
		EEventType.TradeRefused => $"🚫 {botName} trade refused",
		EEventType.GiftReceived => $"🎁 {botName} gift received",
		EEventType.AccountAlert => $"🔔 {botName} account alert",
		EEventType.GameRedeemed => $"🎮 {botName} new game",
		EEventType.BotAdded => $"➕ {botName} added",
		EEventType.BotRemoved => $"➖ {botName} removed",
		_ => $"🔔 {botName}"
	};

	private static string ApplyPlaceholders(string template, Bot bot, string? reason, IReadOnlyDictionary<string, string>? extras) {
		string result = template
			.Replace("{Bot}", bot.BotName, StringComparison.OrdinalIgnoreCase)
			.Replace("{SteamID}", bot.SteamID.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
			.Replace("{Reason}", reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

		if (extras != null) {
			foreach ((string key, string value) in extras) {
				result = result.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
			}
		}

		return result;
	}

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
