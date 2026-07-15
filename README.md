# ASFNotify

Push notifications for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm).

[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE.txt)
[![ASF](https://img.shields.io/badge/ArchiSteamFarm-V6.3.7.0%2B-blueviolet.svg)](https://github.com/JustArchiNET/ArchiSteamFarm)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

ASFNotify is a third-party ASF plugin that sends you a push when something happens to one of your bots: it gets disconnected, needs re-authentication, finishes farming, and so on. Handy if you run ASF headless with a bunch of accounts and don't want to watch the logs to notice a bot dropped off.

It can push to ntfy, Gotify and Apprise (any combination). Apprise on its own covers about 100 more services like Discord, Telegram, Slack, Matrix and email.

## Table of contents

- [Why ASFNotify](#why-asfnotify)
- [Supported backends](#supported-backends)
- [Reported events](#reported-events)
- [How it works](#how-it-works)
- [Requirements & compatibility](#requirements--compatibility)
- [Installation](#installation)
- [Configuration](#configuration)
  - [Configuration reference](#configuration-reference)
  - [Global vs. per-bot](#global-vs-per-bot)
  - [Message templating](#message-templating)
  - [Priority & tag mapping](#priority--tag-mapping)
- [Backend setup guides](#backend-setup-guides)
- [Testing your setup](#testing-your-setup)
- [Updating](#updating)
- [Building from source](#building-from-source)
- [Known limitations](#known-limitations)
- [FAQ](#faq)
- [Troubleshooting](#troubleshooting)
- [Related projects & references](#related-projects--references)
- [License](#license)

## Why ASFNotify

ASF runs fine unattended, but it never reaches out when something goes wrong: an account gets logged out, hits a rate limit, or needs re-authentication. The existing "ASF bots" are the other direction, you send commands in (Telegram relays and the like). I wanted the opposite, a small thing that pushes events out.

That's all this is. It hooks ASF's plugin event callbacks and forwards the interesting ones to a push service. The ASF core isn't touched, and you decide which events you actually want.

## Supported backends

| Backend | What it is | Config keys |
|---------|------------|-------------|
| [ntfy](https://ntfy.sh) | Simple pub/sub push service, public or self-hosted | `Ntfy.Url`, `Ntfy.Token` |
| [Gotify](https://gotify.net) | Self-hosted push server | `Gotify.Url`, `Gotify.Token` |
| [Apprise](https://github.com/caronc/apprise-api) | Notification gateway (REST) for Discord, Telegram, Slack, Matrix, email, and ~100 others | `Apprise.Url`, `Apprise.Tags` |

Enable any combination; each event goes to every configured backend. A backend counts as active once its minimum keys are present (see the [reference](#configuration-reference)).

Want Discord / Telegram / Slack? Run an [apprise-api](https://github.com/caronc/apprise-api) container, point `Apprise.Url` at it and let Apprise fan out. There are copy-paste examples in the [backend setup guides](#backend-setup-guides).

## Reported events

Each event is toggled through the `Events` config list. The default set is the low-noise "needs attention / job done / maintenance" selection; everything else is opt-in.

**Connection & session**

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `Disconnected` | on | High | A bot is disconnected from Steam involuntarily. Voluntary/ASF-initiated disconnects (`!stop`, shutdown, reconnect, i.e. `EResult.OK`) are ignored. Includes the Steam `EResult` reason. |
| `LoginAttention` | on | High | A disconnect whose reason means the bot needs you: bad password, 2FA/Steam Guard, ban, rate-limit, etc. When enabled it replaces `Disconnected` for those reasons so you don't get two pushes. |
| `LoggedOn` | off | Low | A bot logs on. Off by default, since Steam reconnects several times a day. |

**Farming**

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `FarmingFinished` | on | Normal | A bot finishes its card-farming cycle, whether or not it farmed anything. |
| `FarmingStarted` | off | Low | A bot starts or resumes farming. Chatty across many bots. |
| `FarmingStopped` | off | Low | Farming is stopped or interrupted (the account starts a game, or you pause it). |

**Trading** (all opt-in; ASFNotify only watches, it never accepts or declines a trade)

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `TradeOffer` | off | Normal | An incoming trade offer arrives (partner SteamID, item counts, ASF's decision). |
| `TradeAccepted` | off | Normal | ASF accepted one or more incoming offers. |
| `TradeRefused` | off | Low | ASF rejected/blacklisted/ignored one or more incoming offers. |

**Account & social**

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `AccountAlert` | on | High | Steam raised an account alert (security/account status) for a bot. Only the fact that an alert exists is available, not its content. |
| `GiftReceived` | on | Normal | A bot received a Steam gift (claim it before it expires). |

**Bot lifecycle**

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `BotAdded` | off | Low | A new bot is created. The startup burst (every bot on each ASF start) is suppressed, so only genuine later additions notify. Gated by the global config, since per-bot config isn't loaded yet at this point. |
| `BotRemoved` | off | Low | A bot's config is deleted or renamed. Does not fire on shutdown. |

**ASF maintenance** (server-scoped: no specific bot, labelled `ASF`, gated by the global config only)

| Event | Default | Prio | Fires when |
|-------|:-------:|:----:|-------------|
| `AsfStarted` | on | Normal | ArchiSteamFarm finished starting up. Fires once per launch, so you know the process is back. |
| `AsfUpdated` | on | High | ASF finished self-updating (old to new version) and is about to restart. Explains the reconnect wave that follows. |
| `PluginUpdated` | on | Normal | ASF finished updating ASFNotify itself and is restarting to apply it. |

The two update events are sent synchronously right before ASF restarts, so they go out before the process exits.

So the default set is: `Disconnected`, `LoginAttention`, `FarmingFinished`, `AccountAlert`, `GiftReceived`, `AsfStarted`, `AsfUpdated`, `PluginUpdated`. Override it with your own `Events` list (see [Configuration](#configuration)).

## How it works

A few things worth knowing:

- Handlers never block. ASF awaits plugin event handlers, so doing HTTP inside them would hold up your bots. Instead each handler builds a small notification object, drops it into a bounded in-memory queue and returns. One background task drains the queue and does the actual requests. A slow or dead notification server can't slow down or crash your bots.
- The queue is bounded (64 entries). If a backend is down and the queue fills up, the oldest entries are dropped and logged rather than growing memory forever.
- Cooldown. Steam drops connections in waves during maintenance, so to avoid a storm of pushes the plugin suppresses repeats of the same `(bot, event)` within a window (default 5 minutes, `CooldownMinutes: 0` turns it off).
- One retry. A failed delivery is retried once, then logged and dropped. Nothing is persisted across ASF restarts; these are notifications, not an audit log.
- Requests go through ASF's own `WebBrowser`, so they pick up ASF's proxy, TLS and timeout settings. No separate `HttpClient`.
- Official ASF ships trimmed with reflection-based JSON serialization disabled. So the config is read straight off the JSON DOM (`JsonElement`) and payloads are written with `Utf8JsonWriter`, instead of `JsonSerializer` which isn't available at runtime. Skipping this is a classic way to get a plugin that loads and then throws on first use.

## Requirements & compatibility

- ArchiSteamFarm V6.3.7.0 (the version this build targets) or a compatible V6.3.x release on .NET 10. Verified live against V6.3.8.0.
- Any ASF variant/OS that loads plugins (generic `linux-x64`/Docker, `win-x64`, etc.). Only one managed DLL is deployed and there are no native dependencies.
- ASF loads a plugin only if it was built against a compatible major version. If you move ASF across a major version, grab a matching ASFNotify build.

## Installation

1. Download the latest `ASFNotify.zip` from the [releases](https://github.com/Bladeage/asf-notify/releases) page, or [build it yourself](#building-from-source).
2. Extract it into your ASF `plugins` directory so you end up with:
   ```
   <ASF>/plugins/ASFNotify/ASFNotify.dll
   ```
   On the Docker image that's the mounted `plugins` volume, e.g. `./archisteamfarm/plugins/ASFNotify/ASFNotify.dll`.
3. Add an [`ASFNotify` config block](#configuration) to your `ASF.json` and/or a bot config.
4. Restart ASF. On startup you should see something like:
   ```
   InitPlugins() Loading ASFNotify V1.2.0.0...
   [ASFNotify] v1.2.0.0 loaded.
   [ASFNotify] Active backends: … . Reported events: … .
   ```

Deploy only `ASFNotify.dll`. Don't copy `ArchiSteamFarm.dll` or any shared framework assemblies into the plugin folder; that breaks assembly identity and the plugin won't load.

## Configuration

ASFNotify reads a single object under the top-level `ASFNotify` key. Put it in `config/ASF.json` for global settings, and/or in a bot config (`config/<BotName>.json`) to override per bot. Missing keys just disable the matching feature.

### Minimal (Gotify)

```jsonc
{
  // ... your usual ASF.json ...
  "ASFNotify": {
    "Gotify": {
      "Url": "https://gotify.example.com",
      "Token": "AbCdEf0123456789"
    }
  }
}
```

### Full example (all backends and options)

```jsonc
{
  "ASFNotify": {
    "Ntfy": {
      "Url": "https://ntfy.sh/my-secret-asf-topic",
      "Token": ""                        // optional; Bearer token for protected topics
    },
    "Gotify": {
      "Url": "https://gotify.example.com",
      "Token": "AbCdEf0123456789"        // Gotify application token (required)
    },
    "Apprise": {
      "Url": "http://apprise:8000/notify/asf",   // persistent apprise-api config key
      "Tags": null                        // optional Apprise tag filter, e.g. "admins"
    },
    "Events": ["Disconnected", "LoginAttention", "FarmingFinished", "AccountAlert", "GiftReceived", "TradeOffer", "AsfUpdated"],
    "CooldownMinutes": 5,                 // 0 disables the cooldown
    "Templates": {                        // optional per-event message overrides
      "Disconnected": "{Bot} went offline: {Reason}"
    }
  }
}
```

### Configuration reference

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Ntfy.Url` | string (URL) | — | Full topic URL, e.g. `https://ntfy.sh/<topic>`. The topic is the URL path. Active when it includes a topic path; a bare host like `https://ntfy.sh` counts as not configured. |
| `Ntfy.Token` | string | — | Optional token for protected topics, sent as `Authorization: Bearer …`. Never logged. |
| `Gotify.Url` | string (URL) | — | Gotify base URL, no path, e.g. `https://gotify.example.com`. |
| `Gotify.Token` | string | — | Gotify application token. Active when both `Url` and `Token` are set. Never logged. |
| `Apprise.Url` | string (URL) | — | apprise-api notify endpoint, e.g. `http://host:8000/notify/<key>`. Active when set. |
| `Apprise.Tags` | string | — | Optional comma-separated Apprise tag filter. |
| `Events` | string[] | *(see [Reported events](#reported-events))* | Which events to report. Valid names: `Disconnected`, `LoginAttention`, `LoggedOn`, `FarmingStarted`, `FarmingFinished`, `FarmingStopped`, `TradeOffer`, `TradeAccepted`, `TradeRefused`, `AccountAlert`, `GiftReceived`, `BotAdded`, `BotRemoved`, `AsfStarted`, `AsfUpdated`, `PluginUpdated`. Case-insensitive; unknown names are ignored. |
| `CooldownMinutes` | number (0–255) | `5` | Minimum minutes between two notifications for the same bot and event. `0` disables it. |
| `Templates` | object | — | Per-event message overrides, keyed by event name. See [templating](#message-templating). |

All keys are optional and case-insensitive. With no backend configured, the plugin logs a notice at startup and stays idle.

### Global vs. per-bot

The effective config for a bot is the global config with the bot's config on top, merged property by property: any value set in the bot config wins, anything missing falls back to global.

Note that a per-bot `Events` (or `Templates`) value replaces the global one, it doesn't append.

Example: a global Gotify target for everything, but route one chatty account to its own ntfy topic and also notify on its logins:

```jsonc
// config/MyBot.json
{
  "ASFNotify": {
    "Ntfy": { "Url": "https://ntfy.sh/just-this-bot" },
    "Events": ["Disconnected", "FarmingFinished", "LoggedOn"]
  }
}
```

### Message templating

If `Templates` has an entry for an event, its string replaces the default message for that event. Placeholders (case-insensitive):

| Placeholder | Value | Available for |
|-------------|-------|---------------|
| `{Bot}` | Bot name | all events |
| `{SteamID}` | Bot's SteamID64 | all events |
| `{Reason}` | Disconnect reason (`EResult`) | `Disconnected` |
| `{FarmedSomething}` | `True` / `False` | `FarmingFinished` |

Only the message body is templated; the title (and its emoji) is generated per event.

### Priority & tag mapping

Each event has an abstract priority (Low / Normal / High) that each backend maps to its own scale:

- ntfy: Low → `2`, Normal → `3`, High → `5`
- Gotify: Low → `2`, Normal → `5`, High → `8`
- Apprise has no numeric priority; the message type (below) drives its colour/icon.

Each event also gets a fitting ntfy tag (emoji) and Apprise type:

| Event | Prio | ntfy tag | Apprise type |
|-------|:----:|----------|:------------:|
| `Disconnected` | High | `warning` | `warning` |
| `LoginAttention` | High | `rotating_light` | `failure` |
| `AccountAlert` | High | `rotating_light` | `failure` |
| `AsfUpdated` | High | `arrow_up` | `info` |
| `LoggedOn` | Low | `white_check_mark` | `success` |
| `FarmingStarted` | Low | `seedling` | `info` |
| `FarmingFinished` | Normal | `tada` | `success` |
| `FarmingStopped` | Low | `octagonal_sign` | `warning` |
| `TradeOffer` | Normal | `handshake` | `info` |
| `TradeAccepted` | Normal | `white_check_mark` | `success` |
| `TradeRefused` | Low | `no_entry` | `warning` |
| `GiftReceived` | Normal | `gift` | `success` |
| `BotAdded` | Low | `heavy_plus_sign` | `success` |
| `BotRemoved` | Low | `heavy_minus_sign` | `warning` |
| `AsfStarted` | Normal | `rocket` | `success` |
| `PluginUpdated` | Normal | `arrow_up` | `info` |

## Backend setup guides

### ntfy

1. Pick a hard-to-guess topic name (topics on `ntfy.sh` are public unless you self-host with access control).
2. Set `Ntfy.Url` to `https://ntfy.sh/<your-topic>` (or your self-hosted `https://ntfy.example.com/<topic>`).
3. Subscribe to the topic in the ntfy app or web UI.
4. For protected topics, create an access token and put it in `Ntfy.Token`. A publish-only token is enough.

### Gotify

1. In Gotify, create an Application (Apps → Create Application) and copy its token.
2. Set `Gotify.Url` to the server base URL (e.g. `https://gotify.example.com`) and `Gotify.Token` to that application token.
3. Log in to the Gotify app or web UI to receive messages.

### Apprise → Discord, email, Telegram, Slack, and ~100 more

ntfy and Gotify are posted to directly. Everything else (Discord, email, Telegram, Slack, Matrix, …) goes through Apprise: ASFNotify posts each event to your apprise-api instance, which fans it out to the service URLs you configured there.

1. Run apprise-api:
   ```bash
   docker run -d -p 8000:8000 --name apprise caronc/apprise:latest
   ```
2. Open its web UI (`http://<host>:8000`), create a persistent configuration under a key (e.g. `asf`), and add one service URL per line, for example:
   ```
   discord://WebhookID/WebhookToken
   mailtos://myuser:my-app-password@gmail.com
   tgram://123456:ABC-bottoken/987654321
   ```
3. Point ASFNotify at that config key:
   ```jsonc
   "Apprise": { "Url": "http://<host>:8000/notify/asf" }
   ```
   Now every event reaches all three (Discord, email, Telegram) at once.

Popular service URL formats to add to the Apprise config:

| Service | Apprise URL | Where to get the credentials |
|---------|-------------|------------------------------|
| Discord | `discord://WebhookID/WebhookToken` | Channel → Edit → Integrations → Webhooks; the URL ends in `…/webhooks/<id>/<token>` |
| Email (Gmail) | `mailtos://user:app-password@gmail.com` | Google Account → Security → App passwords |
| Email (any SMTP) | `mailtos://user:pass@smtp.host:587?to=you@example.com` | Your mail provider's SMTP settings |
| Telegram | `tgram://bot-token/chat-id` | `@BotFather` for the token; message the bot once, then read its chat id |
| Slack | `slack://TokenA/TokenB/TokenC/#channel` | Slack app → Incoming Webhooks |
| Microsoft Teams | `msteams://TokenA/TokenB/TokenC/` | Teams channel → Workflows / incoming webhook |
| Matrix | `matrixs://user:pass@matrix.org` | Your Matrix homeserver account |
| Pushover | `pover://user-key@app-token` | pushover.net dashboard |
| Signal | `signal://host:port/from-no/to-no` | A running signal-cli REST API |
| Webhook (JSON) | `json://host/path` | Any endpoint that accepts a JSON POST |

Apprise supports around 100 services (SMS, Rocket.Chat, Home Assistant, Pushbullet, Twilio, Mattermost, ntfy, Gotify, and more). The [Apprise wiki](https://github.com/caronc/apprise/wiki) has the full list and each service's exact parameters.

Tip: give a URL a tag in the Apprise config and set `"Apprise": { "Tags": "…" }` in ASFNotify to route notifications to only the tagged subset.

## Testing your setup

From any Steam chat with the bot, or the ASF web-UI command box, send:

```
notifytest
```

Requires `Master` access. ASFNotify sends a test notification through every configured backend for that bot and replies with a per-backend result, e.g.:

```
[ASFNotify] Test → ntfy: OK | Gotify: OK
```

`FAILED` means the HTTP request didn't succeed; check the URL/token and the ASF log for the exact status code.

## Updating

ASFNotify implements ASF's `IGitHubPluginUpdates` interface (`RepositoryName = "Bladeage/asf-notify"`). If you enable plugin updates in ASF (`PluginsUpdateMode` / `PluginsUpdateList` in `ASF.json`), ASF updates ASFNotify from this repo's GitHub releases and matches the release asset to your ASF version. See [ASF plugin updates](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins#updating-plugins).

## Building from source

You need the .NET 10 SDK, or just Docker. ArchiSteamFarm is a git submodule pinned to the targeted ASF version.

```bash
git clone --recursive https://github.com/Bladeage/asf-notify.git
cd asf-notify
dotnet publish ASFNotify -c Release -o out
# -> out/ASFNotify.dll   (copy into <ASF>/plugins/ASFNotify/)
```

If you forgot `--recursive`:

```bash
git submodule update --init --recursive
```

<details>
<summary>Build with Docker (no local SDK)</summary>

```bash
docker run --rm -v "$PWD:/src" -w /src -u "$(id -u):$(id -g)" \
  -e HOME=/tmp -e DOTNET_CLI_HOME=/tmp \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish ASFNotify -c Release -o out
```

The first build also compiles ArchiSteamFarm and takes a few minutes; later builds are quick. Mount a NuGet cache (`-v <cache>:/tmp/.nuget -e NUGET_PACKAGES=/tmp/.nuget`) to speed up repeat builds.
</details>

The `DebugFast` config skips analyzers for fast iteration; `Release` runs the full analyzer set and treats warnings as errors.

## Known limitations

- Steam Guard / 2FA prompts aren't reported directly. ASF has no plugin callback for an "input needed" state. `LoginAttention` is the closest proxy: it classifies auth-related disconnect reasons (bad password, 2FA, ban, rate-limit) and flags them high-priority. It can't catch a prompt that isn't preceded by such a disconnect.
- Steam user-notification events carry no detail. `GiftReceived` and `AccountAlert` come from Steam's notification feed, which only signals that a notification of that type appeared, not what it is. The push just tells you to go check the account.
- Card-drop progress, per-license changes, mobile-confirmation prompts and an ASF-process-down alert aren't available, because ASF has no plugin callback for them. A dead process can't push its own alert either; use an external heartbeat for that.
- Best-effort, not a guaranteed log. If a backend is unreachable the notification is retried once, then dropped and logged. Nothing survives an ASF restart.
- Config is readable through ASF's IPC. Prefer publish-only / scoped tokens for ntfy and Gotify.
- Version coupling. A plugin built against one ASF major version may not load on another (see [compatibility](#requirements--compatibility)).

## FAQ

**Does this work with the official ASF Docker image?**
Yes. Mount the plugin at `plugins/ASFNotify/ASFNotify.dll` in your plugins volume and restart the container.

**Can I get notifications on Discord, email, Telegram, Slack, …?**
Yes, through Apprise: run an apprise-api instance, add those service URLs to a persistent config, and point `Apprise.Url` at it. Copy-paste examples for the popular ones are in [Backend setup guides → Apprise](#apprise--discord-email-telegram-slack-and-100-more).

**Can I use more than one backend at once?**
Yes. ntfy, Gotify and Apprise are independent; configure any combination and each notification goes to all of them.

**Will login notifications spam me?**
They would, which is why `LoggedOn` is off by default (Steam reconnects several times a day). If you enable it, the per-`(bot, event)` cooldown still collapses bursts. Most people are fine with just `Disconnected` and `FarmingFinished`.

**Will this slow down or destabilize my bots?**
No. Handlers don't block on the network; they enqueue and return, and a background worker does the sending. A dead notification server can't affect farming or logins.

**What happens if my notification server is down?**
The delivery is retried once, then logged as a warning and dropped. The bots are unaffected and the plugin keeps running.

**Are my tokens safe?**
They're never written to the log. But ASF config is readable through ASF's IPC API, so use scoped/publish-only tokens where the backend supports it (ntfy tokens can be publish-only; Gotify application tokens can only post).

**Can different bots notify different targets?**
Yes. Put a global config in `ASF.json` and override per bot in `config/<BotName>.json`. The two are merged, bot wins.

**Can I change the wording of the messages?**
Yes, with the `Templates` map and the `{Bot}`, `{SteamID}`, `{Reason}` and `{FarmedSomething}` placeholders. The title/emoji is generated automatically.

**What's the difference between `FarmingFinished` and `FarmingStopped`?**
`FarmingFinished` fires when a bot completes its farming cycle (nothing left to farm). `FarmingStopped` fires when farming is interrupted, e.g. the account starts playing a game or you pause it. The latter is noisier, so it's off by default.

**What is `LoginAttention` and how is it different from `Disconnected`?**
`LoginAttention` is a higher-signal `Disconnected`: it only fires when the disconnect reason means the bot actually needs you (wrong password, 2FA, ban, rate-limit, access denied). When enabled it reports those specific disconnects as `LoginAttention` instead of `Disconnected`, so you don't get two pushes. If it's disabled, those cases fall back to a plain `Disconnected`.

**Does ASFNotify accept or decline trades / friend requests?**
No, it only watches. The trade and friend-request hooks always return "not handled", so ASF's own behaviour is never changed.

**Why did I get both a `TradeOffer` and a `TradeAccepted` push for the same trade?**
They come from two different ASF callbacks: `TradeOffer` when an offer arrives, `TradeAccepted`/`TradeRefused` when ASF finishes processing it. That's why the trade events are separate and opt-in; enable only the ones you care about.

**Will `BotAdded` spam me every time ASF restarts?**
No. `BotAdded` suppresses the startup burst (the one-per-bot batch while ASF loads), so it only notifies for genuine additions afterwards. `BotRemoved` only fires when a config is actually deleted or renamed, not on shutdown.

**Do the `AsfUpdated` / `PluginUpdated` notifications reach me before ASF restarts?**
They're sent synchronously (with a short timeout) when ASF signals the update finished, rather than through the background queue, so they go out before the process restarts. They're server-scoped (labelled `ASF`) and use the global config only.

**Which ASF version do I need?**
A V6.3.x release on .NET 10 (this build targets V6.3.7.0, verified against V6.3.8.0). Plugins must match ASF's major version.

**How do I test that it delivers?**
Send `notifytest` (Master access). It pushes a test through every configured backend and reports `OK`/`FAILED` per backend.

**Is this affiliated with ArchiSteamFarm?**
No, it's an independent third-party plugin. Report ASFNotify issues here, not to the ASF project.

## Troubleshooting

- Plugin doesn't load: check the ASF startup log for `Loading ASFNotify …`. A `ReflectionTypeLoadException` or MEF/composition error usually means an ASF version mismatch; use a build matching your ASF version. Make sure the file is at `plugins/ASFNotify/ASFNotify.dll`.
- `No global backend configured`: ASF loaded the plugin but found no valid backend. Check the `ASFNotify` block exists and a backend has its required keys (ntfy: `Url` with a topic path; Gotify: `Url` + `Token`; Apprise: `Url`).
- No notifications arrive: run `notifytest`. If it says `FAILED`, verify the URL/token and look for `[ASFNotify] … responded with HTTP …` in the log. If it says `OK` but nothing arrives, check that you're subscribed to the right topic/app.
- Fewer notifications than expected: the cooldown (default 5 min) suppresses repeats for the same bot and event. Lower `CooldownMinutes` or set it to `0` while testing.
- Nothing on clean shutdowns: that's intentional; ASF-initiated disconnects (`EResult.OK`) aren't reported, only involuntary ones.

## Changelog

Release history is in [CHANGELOG.md](CHANGELOG.md).

## Related projects & references

ArchiSteamFarm:
- [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm), the Steam card farming bot this plugin extends
- [Plugins overview (wiki)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins)
- [Plugin development (wiki)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Plugins-Development)
- [Third-party plugin list (wiki)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Third-party)
- [ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate), the scaffold this repo started from
- [Configuration (wiki)](https://github.com/JustArchiNET/ArchiSteamFarm/wiki/Configuration)

Notification backends:
- [ntfy](https://ntfy.sh) · [docs](https://docs.ntfy.sh)
- [Gotify](https://gotify.net) · [docs](https://gotify.net/docs/)
- [Apprise](https://github.com/caronc/apprise) · [apprise-api](https://github.com/caronc/apprise-api) · [service URLs](https://github.com/caronc/apprise/wiki)

## License

[Apache-2.0](LICENSE.txt). Not affiliated with, endorsed by, or supported by the ArchiSteamFarm project.
