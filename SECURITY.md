# Security

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private vulnerability reporting instead: the **Security** tab of this repository, then **Report a vulnerability**. Include the steps to reproduce it.

## What this repository contains, and what it does not

- **No secrets.** Model access uses Microsoft Entra ID tokens only, and the Azure OpenAI account has key authentication disabled (`infra/main.bicep`). Nothing in the repository is a key, password or connection string.
- **Generated secrets stay local.** The deployment and the local DIAL stack generate their keys on first use, into git-ignored files: `infra/dial/.keys.json`, `infra/dial/.secrets.json`, `dial/stack/.env`, `dial/stack/keys.json` and `dial/stack/config/core-config.json`. Delete them to rotate everything.
- **Synthetic data only.** Every claim, policy, customer, IBAN, phone number and e-mail address is invented. "Contoso Versicherung AG" is fictional.
- **Evidence packs are redacted.** DIAL keys never appear in them, and employees appear only as pseudonyms.

## Running it safely

- `AuthMode=Dial`, the default, takes identity only from DIAL Core. The development mode (`AuthMode=DevPersonas`) trusts a request header, so it refuses to serve on anything but a loopback address.
- Keep CI pull-request builds off self-hosted agents if the repository is public: a pull request from a fork would run its code on that machine.
- This is a reference implementation. Known gaps and the road to production are listed in [docs/architecture-review.md](docs/architecture-review.md) and [docs/security-and-compliance.md](docs/security-and-compliance.md).
