# Changelog

All notable changes to ASFNotify are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/); the version numbers match the plugin's assembly version.

## [1.4.1.0] – 2026-07-19

Tells free promos, real gifts, key redemptions and purchases apart.

### Added
- **License classification.** A new license is classified by how it entered the account, each class with
  its own event: **`GiftAccepted`** (default on) — a genuine gift, detected by pairing the arriving
  complimentary license with a drop of the gift-inbox counter (measured on real accounts, the license
  data alone cannot tell gifts and free packages apart — everything is "complimentary/single-purchase");
  **`FreeLicenseAdded`** (opt-in) — free packages, guest passes and hardware promos, chatty for
  auto-claimer setups; **`GamePurchased`** (opt-in) — a license paid with a real payment method, useful
  as a security signal on accounts that never buy. `GameRedeemed` keeps covering key redemptions,
  unchanged.

### Changed
- **`GiftReceived` wording is honest now.** Steam's gift-inbox counter can't distinguish a real gift
  from a guest pass, so the push says "item waiting in the gift inbox (gift or guest pass)" instead of
  claiming a Steam gift was received; the truth about the type arrives with `GiftAccepted` /
  `FreeLicenseAdded` once the item is accepted.
- The one-time startup diagnostic now logs the payment-method × license-type matrix per account — the
  ground truth behind the classification.

## [1.4.0.0] – 2026-07-19

The notification-noise release: every default push should now be either actionable or genuinely news.
Config-compatible with 1.3.x — no key was renamed or removed — but several events behave differently,
so read the Changed section.

### Added
- **`GameFarmingStarted`** (opt-in): an individual game starts being farmed, with its name, remaining
  cards and queue position. Announced once per game per farming session; resuming the same game after a
  reconnect stays silent.
- **`MassFarmingStarted`** (opt-in): the bot starts idling a batch of games in parallel to reach
  `HoursUntilCardDrops`. One summary push per farming session (game count, queue totals, estimated
  time), never one per game.
- **`GameFarmingFinished`** (opt-in): all cards of a game have dropped. Finishes inside the cooldown
  window are aggregated into a single push instead of being dropped.
- `FarmingFinished` now carries a session summary: how many games and cards this session actually farmed.
- New template placeholders: `{Game}`, `{CardsRemaining}`, `{QueueCount}`, `{Count}`, `{Cards}`,
  `{Hours}`, `{TotalGames}`, `{TotalCards}`, `{TimeRemaining}`.

### Changed
- **`FarmingFinished` fires only for real farming sessions.** ASF raises its "farming finished" callback
  with nothing farmed at every login and every idle recheck of an already-farmed account; each of those
  used to become a "finished farming / nothing left to farm" push per bot — the single biggest source of
  noise, and factually wrong on the phone. The idle case is now silent, deliberately without a
  replacement event.
- **`Disconnected` is debounced.** A disconnect is reported only if the bot is still offline two minutes
  later, so self-healing blips (the daily Steam disconnect, maintenance flaps) stay silent. Auth
  failures and the rate-limit path are unaffected.
- **`LoginAttention` distinguishes hard from transient failures.** Bans/locks/suspensions report
  immediately; transient reasons (bad password, 2FA mismatch, access denied) need two strikes in a row
  without a successful login in between, because Steam also returns them during hiccups ASF recovers
  from on its own.
- **`LoggedOn` is now a recovery notice.** It fires only after an incident that was actually pushed,
  closing the loop ("back online") instead of narrating every reconnect. Still opt-in.
- **`GiftReceived` and `AccountAlert` are deduplicated by pending count.** ASF forgets its notification
  state on every disconnect, so an unclaimed gift used to be re-announced as "received a gift" at every
  single login. Now only an increase in the pending count reports; after an ASF restart a still-pending
  item reminds you exactly once.
- **`TradeOffer` reports only offers ASF leaves pending** — the ones that actually need review — and is
  now part of the default set. Auto-resolved offers stay with `TradeAccepted`/`TradeRefused`, so a
  single trade can no longer produce two pushes.
- `TradeRefused` counts only rejected and blacklisted offers; ignored (pending) offers belong to
  `TradeOffer` now.
- **`FarmingStarted` was split** into `GameFarmingStarted` + `MassFarmingStarted`. A legacy `Events`
  entry maps to both for now, with a log notice; the mapping will be removed in a future release.
- `FarmingStopped` reports only genuine interruptions (pause, stop command, account in use); the copies
  fired around disconnects, internal re-batches and right before `FarmingFinished` are filtered out.
- Gotify authentication moved from the URL query to the `X-Gotify-Key` header, so the token no longer
  shows up in ASF's debug-level HTTP traces or proxy logs.
- Default event set is now: `Disconnected`, `LoginAttention`, `FarmingFinished`, `TradeOffer`,
  `AccountAlert`, `GiftReceived`, `AsfStarted`, `AsfUpdated`, `PluginUpdated`.

### Fixed
- The once-a-day marker for the rate-limit report is stamped only when a push was actually enqueued, so
  the day is no longer consumed while the event is disabled.
- `{FarmedSomething}` is deprecated (always `True` now); existing templates keep working.

## [1.3.4.0] – 2026-07-15

### Fixed
- A bot stuck behind Steam's login throttle no longer pushes all day. `RateLimitExceeded` was treated as
  an auth failure, so every one of ASF's automatic retries sent a high-priority "needs attention" push —
  roughly one every 25 minutes for as long as Steam kept throttling, which can be hours. It is now
  reported once per bot per day (and again the next day if the bot is still stuck), and the message says
  what is actually happening: ASF keeps retrying on its own, nothing needs doing. The per-event cooldown
  never helped here, because the retries are further apart than the cooldown.

## [1.3.3.0] – 2026-07-15

### Fixed
- `GameRedeemed` no longer misreports free packages as gifts. Free-package grants and Steam gifts both
  arrive as "complimentary" licenses, which the previous `!= None` filter let through and labelled
  "via a gift". `GameRedeemed` now fires only for actual key redemptions (`ActivationCode`) — background
  redeeming and keys forwarded between bots — and genuine gifts remain covered by the `GiftReceived`
  event. The message now reads "Bot X redeemed …". Added a debug log of each new package's payment
  method to make this diagnosable.

## [1.3.2.0] – 2026-07-15

Fixes regressions a re-review found in the 1.3.1 hardening.

### Fixed
- Dispatcher: each send attempt has its own timeout instead of one shared budget, so a single dead
  backend can no longer starve the others or get them mislogged as failed; delivered / failed / timed
  out / skipped are now logged distinctly.
- The cooldown is re-checked at delivery time, restoring burst suppression (a flapping bot collapses to
  one notification per window) while still starting the window only after a successful send.
- `GameRedeemed` maintains its baseline on every valid license list (even while the event is disabled)
  and unions rather than replaces it, so re-enabling the event or a transiently truncated list can't
  replay old acquisitions. Non-OK callbacks are ignored.
- A very large redeem batch is sent as an aggregated "added N new licenses" instead of being dropped.
- Gotify URL handling no longer breaks when the configured URL carries a query or fragment.
- Config parsing warns on wrong-type `Events` / `Templates` values.

### CI
- The publish workflow triggers only on version-shaped tags; `ci.yml` runs a publish smoke test on every push.

## [1.3.1.0] – 2026-07-15

Hardening pass from a full code + publication review.

### Fixed
- `GameRedeemed` no longer reports the whole library when Steam sends an empty/truncated license list,
  and treats an unusually large diff as a resync instead of a mass redemption.
- The cooldown now starts only after a successful delivery, so a failed send no longer suppresses the
  next (possibly important) repeat for the whole window.
- Delivery has a per-notification timeout and a short delay before the single retry, so one unreachable
  backend can't stall the queue for minutes.
- Game-name resolution fetches PICS access tokens and falls back to non-"game" app names, so more
  redemptions show a name instead of a raw package ID.
- Gotify and ntfy URLs behind a reverse-proxy subpath now build the correct endpoint / topic.
- Unknown `Events` names, unknown `Templates` keys and an invalid `CooldownMinutes` are logged instead
  of being silently ignored. Queue evictions and an abnormal consumer exit are logged.

### CI
- The release job fails if the pushed tag doesn't match the plugin version; the publish workflow runs
  only on tags; releases are marked immutable.

## [1.3.0.0] – 2026-07-15

### Added
- `GameRedeemed` event (opt-in): notifies when a new license is added to a bot — a redeemed key (including
  keys forwarded internally between bots) or a gift. Detected via the Steam license list, with free-package
  grants filtered out by payment method. The game name is resolved through PICS when possible, falling back
  to the package ID.

### Changed
- Neutralised the `GiftReceived` and `AccountAlert` messages: they no longer tell you to log in and act
  manually, which is wrong when the account auto-redeems.

## [1.2.0.0] – 2026-07-15

### Added
- `AsfStarted` event: a notification when ArchiSteamFarm has started up (fires once per launch, from
  `OnASFInit`). Server-scoped like the update events, and on by default.

## [1.1.0.0] – 2026-07-14

### Added
- **11 new notification events** (15 total), hooking additional ASF plugin interfaces (connection,
  farming, trade offers, Steam user notifications, bot lifecycle and ASF/plugin updates):
  `LoginAttention`, `FarmingStarted`, `TradeOffer`, `TradeAccepted`, `TradeRefused`,
  `GiftReceived`, `AccountAlert`, `BotAdded`, `BotRemoved`, `AsfUpdated`, `PluginUpdated`.
- `LoginAttention` classifies auth-failure disconnects (bad password, 2FA/Steam Guard, ban, rate-limit)
  as high-priority and **replaces** `Disconnected` for those reasons, so one incident isn't reported twice.
- Trade events are **observe-only** — `OnBotTradeOffer` returns `false`, so ASF's own trade handling is never changed.
- `BotAdded`/`BotRemoved` use a 60s startup grace to suppress the startup batch; they fire only on genuine
  config add/delete, not on shutdown.
- `AsfUpdated`/`PluginUpdated` are server-scoped and sent **synchronously** right before ASF restarts,
  gated by the global config only.
- ntfy tag and Apprise type mapping extended to cover every new event.
- Expanded README (event tables, popular Apprise service examples, FAQ) and this changelog.

### Changed
- Default event set is now the low-noise selection: `Disconnected`, `LoginAttention`, `FarmingFinished`,
  `GiftReceived`, `AccountAlert`, `AsfUpdated`, `PluginUpdated`. Everything else is opt-in.
- CI publishes **full (non-prerelease) releases** with a proper description, so both the Stable and
  Experimental ASF update channels pick the plugin up.

Built against ArchiSteamFarm V6.3.7.0 (.NET 10); verified live on ASF V6.3.8.0.

## [1.0.0.0] – 2026-07-14 (pre-publication)

Never released on its own; the first public release was 1.1.0.0. Listed here for the record.

### Added
- Initial version — event-driven push notifications for ArchiSteamFarm.
- Backends: **ntfy**, **Gotify**, **Apprise** (any combination, config-driven).
- Events: `Disconnected` (involuntary) and `FarmingFinished` on by default; `LoggedOn` and `FarmingStopped` opt-in.
- Channel-based dispatcher: non-blocking handlers, per-`(bot, event)` cooldown, one retry, best-effort delivery.
- Global + per-bot configuration (per-bot overrides) with message templating.
- `notifytest` command (Master access) for end-to-end verification.
- `IGitHubPluginUpdates` for ASF-native plugin updates.
- Trimmed-runtime-safe config parsing (`JsonElement`) and payload building (`Utf8JsonWriter`).

[1.4.1.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.4.1.0
[1.4.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.4.0.0
[1.3.4.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.4.0
[1.3.3.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.3.0
[1.3.2.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.2.0
[1.3.1.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.1.0
[1.3.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.0.0
[1.2.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.2.0.0
[1.1.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.1.0.0
