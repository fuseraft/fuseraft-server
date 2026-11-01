---
title: Configuration
description: Every appsettings.json key and environment variable fuseraft-web reads.
---

Settings come from `appsettings.json` next to the binary, or from environment variables. An
environment variable names a key with `__` between sections, so `Fuseraft:DataDirectory` is
`Fuseraft__DataDirectory`, and the first daemon's token is `Fuseraft__Daemons__0__Token`. Keep
secrets in the environment rather than the file.

## Server

| Key | Default | |
|---|---|---|
| `Fuseraft:Binary` | `fuseraft` | The fuseraft CLI: a path, or a name on `PATH` |
| `Fuseraft:DataDirectory` | `~/.fuseraft-web` | Accounts, cookie-signing keys, saved daemons, and new accounts' homes and workspaces (mode 700) |
| `Fuseraft:RunSchedules` | `true` | Run every account's due [scheduled jobs](/fuseraft-web/guides/more/#schedule) once a minute |
| `Fuseraft:KeyCertificate` | | A `.pfx` with an RSA key that encrypts the data-protection keys. See [What to back up](/fuseraft-web/deploy/backups/) |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Where to listen |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `false` | Trust `X-Forwarded-*` from a [reverse proxy](/fuseraft-web/deploy/reverse-proxy/). Only set it behind one |

## Accounts

| Key | Default | |
|---|---|---|
| `Auth:LocalAccounts` | `true` | Username and password sign-in |
| `Auth:SessionHours` | `12` | How long a sign-in lasts without activity |
| `Auth:DefaultWorkspaceRoot` | `{data}/workspaces/{user}` | A new non-admin's [workspace root](/fuseraft-web/guides/accounts/#users-and-admins). `{data}` is the data directory, `{user}` the username |

## Single sign-on

See [Single sign-on](/fuseraft-web/guides/single-sign-on/). It's on when `Authority` and
`ClientId` are both set.

| Key | Default | |
|---|---|---|
| `Auth:Oidc:Authority` | | The provider's issuer URL |
| `Auth:Oidc:ClientId` | | |
| `Auth:Oidc:ClientSecret` | | Set it in the environment: `Auth__Oidc__ClientSecret` |
| `Auth:Oidc:DisplayName` | `single sign-on` | The sign-in button's label |
| `Auth:Oidc:Scopes` | `openid`, `profile`, `email` | |
| `Auth:Oidc:AutoProvision` | `false` | Create an account on first sign-in for an email in `AllowedEmailDomains` |
| `Auth:Oidc:AllowedEmailDomains` | | Domains (`example.com`) whose verified emails may get accounts |
| `Auth:Oidc:AdminEmails` | | Verified emails that get admin accounts on first sign-in |

## Daemons

`Fuseraft:Daemons` is a list of [daemons](/fuseraft-web/guides/daemons/) to attach to. Each entry:

| Key | Default | |
|---|---|---|
| `Name` | | Shown on the Daemons page |
| `Url` | | The attach WebSocket, e.g. `wss://build-box.lan:24601/attach` |
| `Token` | | The daemon's token. Set it in the environment: `Fuseraft__Daemons__<n>__Token` |
| `CertificateSha256` | | Trust a self-signed `wss://` certificate by its SHA-256 fingerprint |
| `AutoConnect` | `true` | Connect when the server starts, and reconnect after a drop |
| `Users` | | Non-admin usernames who may send it tasks and answer their approvals |

## Environment variables

| Variable | |
|---|---|
| `FUSERAFT_WEB_KEY_CERT_PASSWORD` | The password for `Fuseraft:KeyCertificate` |
| `FUSERAFT_WEB_PASSWORD` | The password for `fuseraft-web users add` and `passwd`, instead of a prompt |
| `FUSERAFT_HOME` | The first account's fuseraft home, instead of `~/.fuseraft` |
