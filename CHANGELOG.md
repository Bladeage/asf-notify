# Changelog

All notable changes to ASFNotify are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/); the version numbers match the plugin's assembly version.

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

[1.3.3.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.3.0
[1.3.2.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.2.0
[1.3.1.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.1.0
[1.3.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.0.0
[1.2.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.2.0.0
[1.1.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.1.0.0
