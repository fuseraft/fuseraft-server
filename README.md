# fuseraft-web

<img src="wwwroot/fuseraft-banner.png" alt="fuseraft — an agent orchestration framework">

A web UI for [fuseraft](https://github.com/fuseraft/fuseraft-cli). Run and watch agent orchestration sessions, chat with the fuseraft REPL, and approve what agents want to do, from a browser, with an account per person.

fuseraft-web doesn't link fuseraft's code. It runs the `fuseraft` CLI and speaks its versioned [wire protocol](https://github.com/fuseraft/fuseraft-cli/blob/main/docs/protocol.md): `--json` queries for lists, and JSONL event streams (`fuseraft run --bridge`, `fuseraft repl --vscode`) for live sessions. A fuseraft upgrade can't break its build, and a mismatched version is reported, not misread.

---

## Quick start

1. Install the [`fuseraft` CLI](https://github.com/fuseraft/fuseraft-cli/releases), v1.13.0 or later.
2. Download the archive for your platform from [Releases](https://github.com/fuseraft/fuseraft-web/releases), unpack it, and run `./fuseraft-web` (`fuseraft-web.exe` on Windows).
3. The log prints a one-time setup link:

   ```
   There are no accounts yet. Create the first admin at http://localhost:5000/setup?code=…
   ```

   Open it and create the first admin. Or create it from a shell: `./fuseraft-web users add <name> --admin`.

It listens on `http://localhost:5000`, so only this machine can reach it until you [expose it deliberately](docs/deploying.md).

---

## Accounts

Every page needs a signed-in account. People sign in with a username and password, with [single sign-on](#single-sign-on-oidc), or with either.

**Each account has its own fuseraft home** (`FUSERAFT_HOME`), so its sessions, REPL chats, memory, schedule and model profiles are its own. Every fuseraft process an account starts runs against its home and gets only its profile keys. The first account gets fuseraft's default home (`~/.fuseraft`), so a machine that used fuseraft before keeps its history. Later accounts get one under the data directory.

| | Users | Admins |
|---|---|---|
| Their own sessions, chats, memory, schedule, profiles | ✓ | ✓ |
| Other people's sessions and approvals | | see and act on them |
| Other people's REPL chats | | |
| Workspaces | only under their workspace roots | anywhere |
| Daemons | the ones an admin shares with them | all, and adds and connects them |
| Users page | | ✓ |

A non-admin's **workspace roots** (by default `<data>/workspaces/<name>`; admins change them on the Users page) bound the directories they can pick, run sessions and chats in, and point configs at.

> **Trust model.** Accounts separate people who trust each other, not strangers. Every agent runs as the server's OS user, so an agent's shell or file tools can reach whatever that user can, including other accounts' homes. Workspace roots bound what the UI lets someone pick, not what an agent can do once it runs. For untrusted users, run one fuseraft-web per OS user or container.

### Managing accounts

Admins manage accounts on the **Users** page: add, reset passwords, promote or demote, disable, remove, and edit workspace roots. Everyone can change their own password and link single sign-on on the **Account** page.

The same works from a shell, including while the server runs, which is also how to recover a locked-out admin:

```bash
fuseraft-web users list
fuseraft-web users add alice --admin     # prompts for a password (or set FUSERAFT_WEB_PASSWORD)
fuseraft-web users passwd alice
fuseraft-web users admin bob on|off
fuseraft-web users disable bob           # ends bob's sign-ins at once
fuseraft-web users enable bob
fuseraft-web users remove bob            # leaves bob's fuseraft home on disk
```

Passwords are at least 10 characters and stored as PBKDF2-SHA256 hashes. Sign-in is rate limited to 10 attempts a minute per address. Changing a password or disabling an account ends that account's sign-ins on its next request, and a role change applies from its next request. The last admin can't be demoted, disabled or removed.

### Single sign-on (OIDC)

Any OpenID Connect provider works: Entra ID, Google, Okta, Keycloak, Authentik and others. Register `https://<your host>/signin-oidc` as a redirect URI, then configure:

```json
"Auth": {
  "Oidc": {
    "Authority": "https://login.example.com/realms/acme",
    "ClientId": "fuseraft-web",
    "DisplayName": "Acme SSO",
    "AutoProvision": true,
    "AllowedEmailDomains": ["acme.com"],
    "AdminEmails": ["alice@acme.com"]
  }
}
```

Put the client secret in the environment rather than the file: `Auth__Oidc__ClientSecret=…`.

An identity signs in as the account it's linked to (matched on issuer and subject, never on email). To link one, sign in with a password and click **Link** on the Account page. With `AutoProvision`, a verified email in `AllowedEmailDomains` or `AdminEmails` gets an account on its first sign-in instead. On a fresh install, an `AdminEmails` address can create the first admin this way. Set `Auth:LocalAccounts` to `false` to allow single sign-on only.

---

## Features

### Sessions
Start and monitor multi-agent orchestration sessions. Each session is its own `fuseraft run --bridge` process, so several can run at once. Agent turns and tool calls stream in as they happen, and commands, file writes and agent handoffs wait for approval. Running sessions can be cancelled, and cancelled or interrupted ones resumed. Past sessions in your fuseraft home (including ones started from the terminal) are listed with their full message history.

### Orchestration Builder
Generate an orchestration config from one of `fuseraft init`'s team templates and launch it. Orchestrations you've run appear at the top of the template picker. Pick a template, a workspace, an output path and, optionally, a model (your model profiles are suggested) and endpoint. The generated config is shown with its validation result.

### Model Profiles
Store model IDs, endpoints and API keys once. API keys go in the OS keychain (GNOME Keyring via `secret-tool`, macOS Keychain, Windows Credential Manager), with a mode-600 file in your fuseraft home as the fallback. A profile's key reaches only the processes you start: as `FUSERAFT_PROFILE_{id}_KEY` for sessions, so `apiKeyEnvVar:` in a config resolves it, and as `FUSERAFT_API_KEY` for REPL chats.

### REPL
Chat with the fuseraft REPL, the same engine as `fuseraft repl` and the VS Code extension. Pick a model profile and a workspace. The chat's tools are confined to the workspace, and file writes and commands ask for approval in the chat (and on the HITL page). Slash commands are handled by the CLI. Replies stream in and render as Markdown. Chats are saved to your fuseraft home after each turn and can be resumed.

### Restarts don't end work
Sessions and chats run in relay processes that outlive fuseraft-web. Restart it, upgrade it, or have it crash, and the next one picks every running session and chat back up, with approvals still waiting asked again. See [docs/deploying.md](docs/deploying.md#restarts-dont-end-sessions).

### HITL (Human-in-the-Loop)
Every approval your sessions, chats and daemon tasks are waiting on, in one place, with a badge count in the nav. Shell commands show the command, file writes show a diff, and agent handoffs show who hands off to whom.

### Daemons
<img src="docs/screenshots/daemons.png" alt="The Daemons page: a connected daemon asking to run npm test, with Approve and Deny buttons">

Attach to a running `fuseraft serve`, on this machine or another, over its attach WebSocket. Send it tasks, follow every task's live progress, and answer approvals for the tasks you sent. Daemons connect when the server starts and reconnect after a dropped connection, with backoff. Daemons added on the page are saved in the data directory, with their tokens encrypted; list the ones you manage as configuration in `appsettings.json`:

```json
"Fuseraft": {
  "Daemons": [
    { "Name": "build-box", "Url": "wss://build-box.lan:24601/attach",
      "CertificateSha256": "3A:9F:…", "Users": ["alice"] }
  ]
}
```

Set a daemon's token in the environment rather than the file: `Fuseraft__Daemons__0__Token=<the daemon's token>`. `"AutoConnect": false` leaves one disconnected until someone presses Connect. Give this server a token of its own with the daemon's `--tokens-file`, so you can revoke it without touching the daemon's other clients. A `read` token there makes the daemon watch-only from here: the page shows its progress, marked *read-only*, with no task box.

**Who can use a daemon.** Admins add, connect and remove daemons, and see every task. To let non-admins use one, list their usernames: `"Users": ["alice", "bob"]` in configuration, or the *Who else may use it* field on the page for one added there. Those users see the daemon, send it tasks, and see and approve only their own tasks; the activity feed still shows every task's progress. Everyone else doesn't see it at all. Who sent each unfinished task is kept in the data directory, so its approvals still go to them after a restart.

**Use `wss://` to another machine.** Start the daemon with `--tls-cert` (fuseraft v1.14.0 or later). A certificate from a CA this server trusts needs nothing more. For a self-signed one, set `CertificateSha256` to the fingerprint `fuseraft serve` prints. A plain `ws://` URL to another machine is marked *unencrypted* on the page and logged at startup, since the token and every task cross the network readable.

**Restarts on either side.** fuseraft-web attaches with a client id it keeps in the data directory. When the connection drops (a restart of fuseraft-web, a network blip), the daemon keeps running the task and waits up to its `--reconnect-grace` (60 seconds by default) for an answer to a pending approval. On reconnect it asks again, and sends the results of tasks that finished meanwhile. If the daemon itself restarts, it resumes the running task from its saved session and runs the queued ones, so after reconnecting the task carries on and its approval is asked again. A daemon older than fuseraft v1.14.0 ignores the client id: the page warns about it, and there a dropped connection still loses the pending approval and a daemon restart loses the task.

### Schedule
Add, enable, disable and remove `fuseraft schedule` jobs. fuseraft-web runs every account's due jobs once a minute (`Fuseraft:RunSchedules`), so no cron entry is needed. **Run** starts a job now as a live session with approvals.

### Plugins, Context, Configs, Memory
Browse every plugin and its functions. Attach reference documents to a workspace for agents to read. List and validate orchestration configs with `fuseraft validate`. Browse and delete the memories your chats and agents keep.

---

## Configuration

Settings come from `appsettings.json` next to the binary, or from environment variables (`Section__Key`, e.g. `Fuseraft__DataDirectory`).

| Setting | Default | |
|---|---|---|
| `Fuseraft:Binary` | `fuseraft` | The fuseraft CLI: a path, or a name on `PATH` |
| `Fuseraft:DataDirectory` | `~/.fuseraft-web` | Accounts, cookie-signing keys, and new accounts' homes and workspaces (mode 700) |
| `Fuseraft:RunSchedules` | `true` | Run every account's due scheduled jobs once a minute |
| `Fuseraft:Daemons` | | `fuseraft serve` daemons to attach to, and the non-admins who may use each |
| `Fuseraft:KeyCertificate` | | A `.pfx` with an RSA key that encrypts the data-protection keys; password in `FUSERAFT_WEB_KEY_CERT_PASSWORD`. See [docs/deploying.md](docs/deploying.md#what-to-back-up) |
| `Auth:LocalAccounts` | `true` | Username and password sign-in |
| `Auth:SessionHours` | `12` | How long a sign-in lasts without activity |
| `Auth:DefaultWorkspaceRoot` | `{data}/workspaces/{user}` | A new non-admin's workspace root |
| `Auth:Oidc:*` | | [Single sign-on](#single-sign-on-oidc) |
| `ASPNETCORE_URLS` | `http://localhost:5000` | Where to listen |

To serve other machines, put fuseraft-web behind a TLS reverse proxy: see [docs/deploying.md](docs/deploying.md).

---

## Requirements

- The `fuseraft` CLI, v1.13.0 or later (wire protocol version 1). On `PATH`, or set `Fuseraft:Binary`. The Daemons page needs daemons on v1.13.0 or later too, for the attach WebSocket, and v1.14.0 or later for `wss://`, for picking tasks up again after a reconnect, and for keeping them across a daemon restart. With a v1.13.0 daemon, a dropped connection still loses the task's pending approval, and the page says so.
- Linux: `secret-tool` (from `libsecret-tools`) to keep API keys in the keychain; otherwise they fall back to mode-600 files.
- Release archives are self-contained. Building from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

---

## Building

```bash
./build.sh                      # build + test
./build.sh publish              # framework-dependent build in artifacts/publish/portable/
./build.sh publish linux-x64    # self-contained single binary in artifacts/publish/linux-x64/
./build.sh pack linux-x64       # …and archived as artifacts/fuseraft-web-<version>-linux-x64.tar.gz
```

On Windows, `.\build.ps1` takes the same targets. The version comes from the latest `v*` git tag. CI builds and tests on Linux, macOS and Windows; pushing a `v*` tag publishes a GitHub release with archives for linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64.

To run from source, `dotnet run` (with `ASPNETCORE_ENVIRONMENT=Development` from `Properties/launchSettings.json`).

---

## License

MIT — see [LICENSE](LICENSE).
