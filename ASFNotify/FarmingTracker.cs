using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ArchiSteamFarm.Collections;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Cards;
using ASFNotify.Data;

namespace ASFNotify;

// Derives the per-game farming events (GameFarmingStarted, GameFarmingFinished, MassFarmingStarted)
// and the end-of-session summary for FarmingFinished. ASF has no per-game plugin hooks, so this
// watches the live collections behind CardsFarmer.CurrentGamesFarmingReadOnly/GamesToFarmReadOnly
// through their OnModified event. Getting at that event needs a cast to ASF's concrete collection
// types - an implementation detail rather than a contract - so the cast is guarded and a 60s
// polling fallback covers the day it stops working.
internal sealed class FarmingTracker : IDisposable {
	private const int PollSeconds = 60;

	// Extra spacing between two aggregated GameFarmingFinished pushes; games finishing inside the
	// window join one push instead of each sending their own.
	private const int FlushSpacingBufferSeconds = 15;

	private static int LoggedSubscribeFallback;

	private readonly Bot Bot;
	private readonly ASFNotify Plugin;
	private readonly object Sync = new();

	private bool Active;
	private bool Disposed;

	// Live collections once the guarded cast succeeded; null means polling mode.
	private ConcurrentHashSet<Game>? HookedCurrent;
	private ConcurrentList<Game>? HookedQueue;
	private Timer? PollTimer;

	// Session state, kept across Stop/Start pairs (reconnects, pauses) and reset only when a farming
	// session actually ends via OnBotFarmingFinished.
	private readonly HashSet<uint> AnnouncedGames = [];
	private readonly Dictionary<uint, ushort> FirstSeenCards = [];
	private bool MassAnnounced;
	private int SessionGamesFinished;
	private long SessionCardsFarmed;

	// Last observed queue, to derive removals (= a game finished or was dropped).
	private Dictionary<uint, Game> LastQueue = [];

	// Finished games waiting to be flushed as one aggregated push instead of being cooldown-dropped.
	private readonly List<Game> PendingFinished = [];
	private DateTime LastFinishFlushUtc = DateTime.MinValue;
	private Timer? FlushTimer;

	internal FarmingTracker(Bot bot, ASFNotify plugin) {
		Bot = bot;
		Plugin = plugin;
	}

	// Bot instances are recreated on config reloads; a tracker bound to the old instance watches dead
	// collections and must be replaced.
	internal bool IsFor(Bot bot) => ReferenceEquals(Bot, bot);

	public void Dispose() {
		lock (Sync) {
			Disposed = true;
			Active = false;

			Unhook();

			FlushTimer?.Dispose();
			FlushTimer = null;
		}
	}

	internal void OnFarmingStarted() {
		lock (Sync) {
			if (Disposed) {
				return;
			}

			Active = true;

			HookedCurrent = Bot.CardsFarmer.CurrentGamesFarmingReadOnly as ConcurrentHashSet<Game>;
			HookedQueue = Bot.CardsFarmer.GamesToFarmReadOnly as ConcurrentList<Game>;

			if ((HookedCurrent != null) && (HookedQueue != null)) {
				HookedCurrent.OnModified += OnCurrentModified;
				HookedQueue.OnModified += OnQueueModified;
			} else {
				HookedCurrent = null;
				HookedQueue = null;

				if (Interlocked.Exchange(ref LoggedSubscribeFallback, 1) == 0) {
					ASF.ArchiLogger.LogGenericWarning("[ASFNotify] CardsFarmer collections are not the expected types; per-game farming events fall back to polling.");
				}

				PollTimer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(PollSeconds), TimeSpan.FromSeconds(PollSeconds));
			}

			// The farm task launches in the background just before this hook runs, so the first game(s)
			// may already be in place - process the current state instead of waiting for the next change.
			SyncQueue(SnapshotQueue());
			ProcessCurrent(SnapshotCurrent());
		}
	}

	internal void OnFarmingStopped() {
		lock (Sync) {
			Active = false;

			Unhook();
		}
	}

	// Returns what this session actually farmed; called for both natural completion and the
	// nothing-to-farm case, and resets the per-session state either way.
	internal (int Games, long Cards) OnFarmingFinished() {
		lock (Sync) {
			FlushPendingFinishedLocked();

			(int, long) summary = (SessionGamesFinished, SessionCardsFarmed);

			AnnouncedGames.Clear();
			FirstSeenCards.Clear();
			MassAnnounced = false;
			SessionGamesFinished = 0;
			SessionCardsFarmed = 0;
			LastQueue = [];

			return summary;
		}
	}

	private void Unhook() {
		if (HookedCurrent != null) {
			HookedCurrent.OnModified -= OnCurrentModified;
			HookedCurrent = null;
		}

		if (HookedQueue != null) {
			HookedQueue.OnModified -= OnQueueModified;
			HookedQueue = null;
		}

		PollTimer?.Dispose();
		PollTimer = null;
	}

	// OnModified handlers run synchronously on ASF's farming thread: snapshot, update state, enqueue
	// through the non-blocking dispatcher, never block.
	private void OnCurrentModified(object? sender, EventArgs e) {
		List<Game> current = SnapshotCurrent();

		lock (Sync) {
			if (!Active) {
				return;
			}

			ProcessCurrent(current);
		}
	}

	private void OnQueueModified(object? sender, EventArgs e) {
		Dictionary<uint, Game> queue = SnapshotQueue();

		lock (Sync) {
			if (!Active) {
				LastQueue = queue;

				return;
			}

			ProcessQueue(queue);
		}
	}

	private void Poll() {
		lock (Sync) {
			if (!Active) {
				return;
			}

			ProcessQueue(SnapshotQueue());
			ProcessCurrent(SnapshotCurrent());
		}
	}

	private List<Game> SnapshotCurrent() => Bot.CardsFarmer.CurrentGamesFarmingReadOnly.ToList();

	private Dictionary<uint, Game> SnapshotQueue() {
		Dictionary<uint, Game> result = [];

		foreach (Game game in Bot.CardsFarmer.GamesToFarmReadOnly) {
			result[game.AppID] = game;
		}

		return result;
	}

	private void SyncQueue(Dictionary<uint, Game> queue) {
		foreach ((uint appID, Game game) in queue) {
			if (!FirstSeenCards.ContainsKey(appID)) {
				FirstSeenCards[appID] = game.CardsRemaining;
			}
		}

		LastQueue = queue;
	}

	// A single game farming right now = solo card farming; several = the hours-boost batch of the
	// complex algorithm (HoursUntilCardDrops).
	private void ProcessCurrent(List<Game> current) {
		if (current.Count == 1) {
			Game game = current[0];

			if (AnnouncedGames.Add(game.AppID)) {
				int queueCount = Bot.CardsFarmer.GamesToFarmReadOnly.Count;

				Plugin.ReportFarming(
					Bot,
					EEventType.GameFarmingStarted,
					ENotificationPriority.Low,
					$"▶️ {Bot.BotName} farming {game.GameName}",
					$"Bot {Bot.BotName} started farming {game.GameName} ({game.CardsRemaining} cards left, {queueCount} games in queue).",
					new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
						{ "Game", game.GameName },
						{ "CardsRemaining", game.CardsRemaining.ToString(CultureInfo.InvariantCulture) },
						{ "QueueCount", queueCount.ToString(CultureInfo.InvariantCulture) }
					}
				);
			}
		} else if ((current.Count > 1) && !MassAnnounced) {
			MassAnnounced = true;

			List<Game> queue = Bot.CardsFarmer.GamesToFarmReadOnly.ToList();
			int totalGames = queue.Count;
			long totalCards = queue.Sum(static game => (long) game.CardsRemaining);
			byte hours = Bot.BotConfig.HoursUntilCardDrops;
			string timeRemaining = FormatDuration(Bot.CardsFarmer.TimeRemaining);

			Plugin.ReportFarming(
				Bot,
				EEventType.MassFarmingStarted,
				ENotificationPriority.Low,
				$"⏫ {Bot.BotName} hours-boosting {current.Count} games",
				$"Bot {Bot.BotName} is idling {current.Count} games in parallel to reach {hours} h for card drops (queue: {totalGames} games / {totalCards} cards, est. {timeRemaining}).",
				new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
					{ "Count", current.Count.ToString(CultureInfo.InvariantCulture) },
					{ "Hours", hours.ToString(CultureInfo.InvariantCulture) },
					{ "TotalGames", totalGames.ToString(CultureInfo.InvariantCulture) },
					{ "TotalCards", totalCards.ToString(CultureInfo.InvariantCulture) },
					{ "TimeRemaining", timeRemaining }
				}
			);
		}
	}

	private void ProcessQueue(Dictionary<uint, Game> queue) {
		List<Game> removed = [];

		foreach ((uint appID, Game game) in LastQueue) {
			if (!queue.ContainsKey(appID)) {
				removed.Add(game);
			}
		}

		// The new queue state must be in place before a flush might fire, so the "games left" count in
		// the message doesn't include the game that just left.
		SyncQueue(queue);

		// Exactly one game leaving the queue with zero cards left is a genuine completion (FarmSolo
		// removes a game only after its last drop, and refreshes CardsRemaining right before). Bulk
		// removals are interruptions or queue rebuilds; single removals with cards left are games that
		// became unplayable or hit MaxFarmingTime - neither is a "finished" story.
		if ((removed.Count == 1) && (removed[0].CardsRemaining == 0)) {
			Game game = removed[0];

			SessionGamesFinished++;
			SessionCardsFarmed += FirstSeenCards.GetValueOrDefault(game.AppID);

			PendingFinished.Add(game);
			ScheduleFinishFlushLocked();
		}
	}

	// Finishes inside the cooldown window are collected and sent as one push instead of being
	// silently dropped by the per-(bot,event) cooldown.
	private void ScheduleFinishFlushLocked() {
		TimeSpan spacing = TimeSpan.FromMinutes(Plugin.GetCooldownMinutes(Bot)) + TimeSpan.FromSeconds(FlushSpacingBufferSeconds);
		TimeSpan wait = LastFinishFlushUtc + spacing - DateTime.UtcNow;

		if (wait <= TimeSpan.Zero) {
			FlushPendingFinishedLocked();

			return;
		}

		if (FlushTimer == null) {
			FlushTimer = new Timer(_ => {
				lock (Sync) {
					FlushTimer?.Dispose();
					FlushTimer = null;

					if (!Disposed) {
						FlushPendingFinishedLocked();
					}
				}
			}, null, wait, Timeout.InfiniteTimeSpan);
		}
	}

	private void FlushPendingFinishedLocked() {
		if (PendingFinished.Count == 0) {
			return;
		}

		LastFinishFlushUtc = DateTime.UtcNow;

		int queueCount = LastQueue.Count;
		string names = string.Join(", ", PendingFinished.Select(static game => game.GameName));

		string title;
		string message;

		if (PendingFinished.Count == 1) {
			title = $"🃏 {Bot.BotName} finished {PendingFinished[0].GameName}";
			message = $"All cards for {PendingFinished[0].GameName} have dropped ({queueCount} games left to farm).";
		} else {
			title = $"🃏 {Bot.BotName} finished {PendingFinished.Count} games";
			message = $"All cards have dropped for: {names} ({queueCount} games left to farm).";
		}

		Plugin.ReportFarming(
			Bot,
			EEventType.GameFarmingFinished,
			ENotificationPriority.Low,
			title,
			message,
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
				{ "Game", names },
				{ "QueueCount", queueCount.ToString(CultureInfo.InvariantCulture) }
			}
		);

		PendingFinished.Clear();
	}

	internal static string FormatDuration(TimeSpan duration) {
		if (duration.TotalDays >= 1) {
			return $"{(int) duration.TotalDays}d {duration.Hours}h";
		}

		return duration.TotalHours >= 1 ? $"{(int) duration.TotalHours}h {duration.Minutes}m" : $"{Math.Max(1, duration.Minutes)}m";
	}
}
