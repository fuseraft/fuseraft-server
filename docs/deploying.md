# Deploying fuseraft-web

fuseraft-web listens on `http://localhost:5000` by default, so only its own machine can reach it. To serve a team, run it as a service and put a TLS reverse proxy in front of it. Read the [trust model](../README.md#accounts) first: every agent runs as the service's OS user.

## As a systemd service

Create a dedicated user, so agents can reach only that user's files:

```bash
sudo useradd --system --create-home --home-dir /var/lib/fuseraft-web fuseraft-web
sudo mkdir -p /opt/fuseraft-web
sudo tar -xzf fuseraft-web-<version>-linux-x64.tar.gz -C /opt/fuseraft-web
```

Install the `fuseraft` CLI where that user can run it. Then create `/etc/systemd/system/fuseraft-web.service`:

```ini
[Unit]
Description=fuseraft-web
After=network.target

[Service]
User=fuseraft-web
WorkingDirectory=/opt/fuseraft-web
ExecStart=/opt/fuseraft-web/fuseraft-web
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
Environment=Fuseraft__Binary=/usr/local/bin/fuseraft
# Environment=Auth__Oidc__ClientSecret=…   (or an EnvironmentFile= with mode 600)
Restart=on-failure
# Sessions and chats run in relay processes that outlive the server (see below); stop only the server itself.
KillMode=process

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now fuseraft-web
journalctl -u fuseraft-web | grep setup   # the first admin's setup link
```

Or create the first admin from a shell, as the service user, so it writes the same data directory:

```bash
sudo -u fuseraft-web /opt/fuseraft-web/fuseraft-web users add alice --admin
```

Without a desktop session, `secret-tool` has no keyring to use, so API keys fall back to mode-600 files in each account's fuseraft home.

## Restarts don't end sessions

Each session and REPL chat runs in a small relay process (`fuseraft-web relay`) that holds the fuseraft process and keeps a log. Stopping, restarting, upgrading or crashing the server leaves them running. The next server finds them, replays their logs to rebuild their pages, and asks again any approvals still waiting. A session that needs an approval just waits until someone answers it.

- **systemd:** keep `KillMode=process` in the unit. The default kills everything in the service's control group, relays included. To end everything on purpose, close the chats and cancel the sessions in the UI, or stop the relays with `pkill -f 'fuseraft-web relay'`.
- **Where they live:** logs and metadata in `<data>/relays/<id>/` (no secrets: API keys reach the relay only through its environment), sockets in `$XDG_RUNTIME_DIR/fuseraft-web/` or the temp directory. A relay's files go when its process ends.
- **Not covered:** a reboot, or killing the relays themselves, ends the sessions. A session can then be resumed from the Sessions page, and a chat from its snapshot.

## Behind a reverse proxy

Terminate TLS at the proxy and forward to `127.0.0.1:5000`. The proxy must pass WebSockets, because every page runs over a Blazor Server connection. `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (above) makes fuseraft-web trust the proxy's `X-Forwarded-Proto` and `X-Forwarded-For`, so sign-in cookies are marked secure and rate limiting sees real client addresses. Only set it when a proxy sits in front, because clients could otherwise forge those headers.

Caddy:

```
fuseraft.example.com {
    reverse_proxy 127.0.0.1:5000
}
```

nginx:

```nginx
server {
    listen 443 ssl;
    server_name fuseraft.example.com;
    # ssl_certificate …; ssl_certificate_key …;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 1h;   # long-lived Blazor and approval connections
    }
}
```

fuseraft-web logs a warning at startup if it listens on plain HTTP on anything other than loopback.

## What to back up

- **The data directory** (`~/.fuseraft-web`, or `Fuseraft:DataDirectory`): `users.json`, the cookie-signing `keys/`, and later accounts' fuseraft homes and default workspaces. Losing `keys/` only signs everyone out; losing `users.json` loses the accounts.
- **The first account's fuseraft home** (`~/.fuseraft` of the service user, or `FUSERAFT_HOME`).
- **API keys** held in the OS keychain, if one is in use. Otherwise they're already in each home's `profile-keys/`.

Running sessions and open REPL chats live in their relays, so they survive the service stopping, but not a reboot. Daemons added on the page live in memory and are forgotten when the service stops.

## Upgrading

Stop the service, unpack the new archive over `/opt/fuseraft-web` (keep your `appsettings.json` edits, or use environment variables instead), and start it. Keep the `fuseraft` CLI on a release that speaks the same wire protocol version; a mismatch is reported in the UI and the log. Sessions and chats already running keep their relays from the old binary until they end, and the new server takes them over.
