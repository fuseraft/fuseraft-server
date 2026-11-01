---
title: Daemons
description: Attach to fuseraft serve daemons on this machine or another, send them tasks, share them with users, and keep work across restarts.
---

A daemon is a `fuseraft serve` process: fuseraft running in the background, working a queue of
tasks. See the fuseraft [serve docs](/fuseraft-cli/serve/). fuseraft-web attaches to daemons over
their attach WebSocket, on this machine or another. From the **Daemons** page you send them tasks,
follow every task's live progress, and answer approvals for the tasks you sent.

Daemons connect when the server starts and reconnect with backoff after a dropped connection.

## Start a daemon

On the daemon's machine, listen beyond loopback with a TLS certificate and a tokens file:

```bash
fuseraft serve --bind 0.0.0.0 --http-port 24601 \
  --tls-cert /etc/fuseraft/serve.crt --tls-key /etc/fuseraft/serve.key \
  --tokens-file /etc/fuseraft/tokens
#   TLS certificate CN=build-box.lan, SHA-256 3A:9F:…
```

The tokens file holds one `name token [full|read]` per line. The daemon refuses it unless it's mode
600, and re-reads it when it changes:

```
# name     token (16+ chars)   scope (full by default)
web        3f9a...c2
dashboard  d41c...9e           read
```

Give fuseraft-web a token of its own, so you can revoke it without touching the daemon's other
clients. The flags are covered in full in the fuseraft [serve docs](/fuseraft-cli/serve/).

## Add a daemon

Admins add a daemon on the Daemons page with its name, its attach URL
(`wss://build-box.lan:24601/attach`), its token and, for a self-signed certificate, its
fingerprint. Daemons added there are saved in the data directory, with their tokens encrypted.

To manage daemons as configuration instead, list them in `appsettings.json`:

```json
"Fuseraft": {
  "Daemons": [
    { "Name": "build-box", "Url": "wss://build-box.lan:24601/attach",
      "CertificateSha256": "3A:9F:…", "Users": ["alice"] }
  ]
}
```

Set each daemon's token in the environment rather than the file:

```bash
Fuseraft__Daemons__0__Token=<the daemon's token>
```

`"AutoConnect": false` leaves a daemon disconnected until someone presses **Connect**. Every key is
listed in [Configuration](/fuseraft-web/reference/config/#daemons).

## Use `wss://` to another machine

Start the daemon with `--tls-cert` (fuseraft v1.14.0 or later).

- A certificate from a CA this server trusts needs nothing more.
- For a self-signed one, set `CertificateSha256` to the fingerprint `fuseraft serve` prints.
  fuseraft-web then trusts that certificate and no other.

A plain `ws://` URL to another machine is marked *unencrypted* on the page and logged at startup,
since the token and every task cross the network readable. `ws://` to this machine is fine.

## Who can use a daemon

Admins add, connect and remove daemons, and see every task. To let non-admins use one, list their
usernames: `"Users": ["alice", "bob"]` in configuration, or the **Who else may use it** field on the
page for a daemon added there.

Those users see the daemon, send it tasks, and see and approve only their own tasks. The activity
feed still shows every task's progress. Everyone else doesn't see the daemon at all.

Who sent each unfinished task is kept in the data directory, so its approvals still go to them after
a restart.

## Read-only tokens

A `read` token in the daemon's tokens file makes the daemon watch-only from fuseraft-web. The page
shows its progress, marked *read-only*, with no task box. The daemon refuses tasks from that token
and never asks it to approve anything. Changing a token's scope in the file closes the connections
made with it.

## Restarts on either side

fuseraft-web attaches with a client id it keeps in the data directory, so a daemon knows it again
after a reconnect.

- **fuseraft-web restarts, or the network drops:** the daemon keeps running the task, and waits up
  to its `--reconnect-grace` (60 seconds by default) for an answer to a pending approval. On
  reconnect it asks again, and sends the results of tasks that finished meanwhile.
- **The daemon restarts:** it resumes the running task from its saved session and runs the queued
  ones. After reconnecting, the task carries on and its approval is asked again. A task interrupted
  more than three times is marked failed.

A daemon older than fuseraft v1.14.0 ignores the client id. The page warns about it: there, a
dropped connection still loses the pending approval, and a daemon restart loses the task.
