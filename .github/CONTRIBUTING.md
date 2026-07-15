# Contributing

Thanks for your interest in improving **ASFNotify**! Bug reports, feature ideas, docs, and code are all welcome.

## Reporting bugs & requesting features

Open an [issue](https://github.com/Bladeage/asf-notify/issues). For bugs, please include:

- your ASF version and ASFNotify version (the `[ASFNotify] vX loaded.` log line),
- the backend(s) you use (ntfy / Gotify / Apprise),
- the relevant ASF log lines (redact tokens), and
- what you expected vs. what happened.

## Pull requests

1. Fork and clone with `--recursive` (ASF is a submodule): `git clone --recursive …`.
2. Match the existing code style — the project inherits ASF's analyzers and `.editorconfig`, and a `Release` build treats warnings as errors, so run `dotnet build ASFNotify -c Release` before opening the PR.
3. Keep changes focused and describe the user-facing effect. If you add or change an event, update the `Events` list and tables in the [README](../README.md) and add a [CHANGELOG](../CHANGELOG.md) entry.

Note: ASF's internal plugin API is not guaranteed stable across versions — keep event handlers defensive and best-effort so a failure never affects the running bots.

By contributing you agree that your contributions are licensed under the project's [Apache-2.0](../LICENSE.txt) license.
