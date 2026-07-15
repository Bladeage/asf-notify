## ASFNotify

Event-driven **push notifications** for [ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm) — get pushed the moment a bot disconnects, needs attention (bad password / 2FA / ban), finishes farming, receives a trade offer or gift, and more.

Delivers through **[ntfy](https://ntfy.sh)**, **[Gotify](https://gotify.net)**, and **[Apprise](https://github.com/caronc/apprise-api)** (Discord, email, Telegram, Slack, Matrix, … ~100 services) — any combination at once.

### Installation

1. Download **`ASFNotify.zip`** from the assets below.
2. Extract it into your ASF `plugins` folder so you have `plugins/ASFNotify/ASFNotify.dll`.
3. Add an `ASFNotify` block to `config/ASF.json` (or a bot config) — see the [configuration guide](https://github.com/Bladeage/asf-notify#configuration).
4. Restart ASF, then run the `notifytest` command (Master access) to confirm delivery.

You can verify the download against `SHA512SUMS`.

### Compatibility

Built against **ArchiSteamFarm V6.3.7.0** (.NET 10); use a build matching your ASF major version. With plugin updates enabled (`PluginsUpdateMode`), ASF updates ASFNotify automatically from these releases.

📖 **Full documentation — all events, config reference, backend setup & FAQ: [README](https://github.com/Bladeage/asf-notify#readme)**
