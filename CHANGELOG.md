# Changelog

All notable changes to ASFNotify are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/); the version numbers match the plugin's assembly version.

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
- **11 new notification events** (15 total) across ASF's `IBotTradeOffer2`, `IBotTradeOfferResults`,
  `IBotUserNotifications`, `IUpdateAware` and `IPluginUpdates` hooks:
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

## [1.0.0.0] – 2026-07-14

### Added
- Initial release — event-driven push notifications for ArchiSteamFarm.
- Backends: **ntfy**, **Gotify**, **Apprise** (any combination, config-driven).
- Events: `Disconnected` (involuntary) and `FarmingFinished` on by default; `LoggedOn` and `FarmingStopped` opt-in.
- Channel-based dispatcher: non-blocking handlers, per-`(bot, event)` cooldown, one retry, best-effort delivery.
- Global + per-bot configuration (per-bot overrides) with message templating.
- `notifytest` command (Master access) for end-to-end verification.
- `IGitHubPluginUpdates` for ASF-native plugin updates.
- Trimmed-runtime-safe config parsing (`JsonElement`) and payload building (`Utf8JsonWriter`).

[1.3.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.3.0.0
[1.2.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.2.0.0
[1.1.0.0]: https://github.com/Bladeage/asf-notify/releases/tag/1.1.0.0
