---
title: Run it as a service
description: Run fuseraft-web as a systemd service under its own OS user, and upgrade it.
---

fuseraft-web listens on `http://localhost:5000` by default, so only its own machine can reach it. To serve a team, run it as a service and put a [TLS reverse proxy](/fuseraft-web/deploy/reverse-proxy/) in front of it.

:::caution
Read the [trust model](/fuseraft-web/guides/accounts/#users-and-admins) first: every agent runs as the service's OS user.
:::

## systemd

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
# Sessions and chats run in relay processes that outlive the server (see [Restarts](/fuseraft-web/deploy/restarts/)); stop only the server itself.
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

## Upgrading

Stop the service, unpack the new archive over `/opt/fuseraft-web` (keep your `appsettings.json` edits, or use environment variables instead), and start it. Keep the `fuseraft` CLI on a release that speaks the same wire protocol version; a mismatch is reported in the UI and the log. Sessions and chats already running keep their relays from the old binary until they end, and the new server takes them over.
