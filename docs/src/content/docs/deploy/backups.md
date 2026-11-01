---
title: What to back up
description: The data directory, the key-encryption certificate, fuseraft homes and API keys.
---

- **The data directory** (`~/.fuseraft-web`, or `Fuseraft:DataDirectory`): `users.json`, the data-protection `keys/`, `daemons.json`, and later accounts' fuseraft homes and default workspaces. Losing `keys/` signs everyone out and makes the daemon tokens in `daemons.json` unreadable (re-add those daemons); losing `users.json` loses the accounts. The tokens in `daemons.json` are encrypted with `keys/`, and by default `keys/` itself is stored unencrypted (except on Windows, where DPAPI encrypts it), so keep the directory private to the service user, as fuseraft-web does (mode 700).
- **To encrypt `keys/` too,** give fuseraft-web a certificate kept somewhere else, such as a systemd credential or a mounted secret, so a copy of the data directory alone can't forge sign-ins or decrypt daemon tokens:

  ```bash
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj /CN=fuseraft-web-keys -keyout k.key -out k.crt
  openssl pkcs12 -export -inkey k.key -in k.crt -out /etc/fuseraft-web/keys.pfx && rm k.key
  ```

  ```ini
  Environment=Fuseraft__KeyCertificate=/etc/fuseraft-web/keys.pfx
  Environment=FUSERAFT_WEB_KEY_CERT_PASSWORD=…   (or an EnvironmentFile= with mode 600)
  ```

  Keys made from then on are encrypted with it; ones made before stay readable as they are until they expire (90 days). Back the certificate up separately: without it, `keys/` can't be read.
- **The first account's fuseraft home** (`~/.fuseraft` of the service user, or `FUSERAFT_HOME`).
- **API keys** held in the OS keychain, if one is in use. Otherwise they're already in each home's `profile-keys/`.

Running sessions and open REPL chats live in their relays, so they survive the service stopping, but not a reboot. Daemons, and the tasks you sent them, live on the daemons' machines: after a restart fuseraft-web reconnects and picks up the running task, its pending approval and any results it missed (see [Daemons](/fuseraft-web/guides/daemons/#restarts-on-either-side)). A daemon keeps its own queue in its fuseraft home (`state/<project>/serve-queue.json`), so back that up on the daemon's machine if a lost disk there shouldn't lose queued tasks.
