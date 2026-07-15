# Security policy

## Supported versions

The latest released version of ASFNotify receives fixes. Always run a build matching your ArchiSteamFarm major version.

## Reporting a vulnerability

Please report security issues **privately** — do not open a public issue for them.

- Preferred: use GitHub's private vulnerability reporting on this repo's **Security → Report a vulnerability** page ([how it works](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)).
- Please include affected version(s), reproduction steps, and impact.

You'll get an acknowledgement as soon as possible, and a fix or mitigation will be released once confirmed.

## Scope & handling secrets

ASFNotify sends notifications through third-party services using tokens you configure. Tokens are never written to the log, but **anything in ASF config is readable via ASF's IPC interface** — prefer publish-only / scoped tokens for ntfy and Gotify. Never paste real tokens into issues or PRs.
