---
title: Restarts don't end work
description: Sessions and chats run in relay processes that outlive the server, and what that means for systemd.
---

Each session and REPL chat runs in a small relay process (`fuseraft-web relay`) that holds the fuseraft process and keeps a log. Stopping, restarting, upgrading or crashing the server leaves them running. The next server finds them, replays their logs to rebuild their pages, and asks again any approvals still waiting. A session that needs an approval just waits until someone answers it.

- **systemd:** keep `KillMode=process` in the unit. The default kills everything in the service's control group, relays included. To end everything on purpose, close the chats and cancel the sessions in the UI, or stop the relays with `pkill -f 'fuseraft-web relay'`.
- **Where they live:** logs and metadata in `<data>/relays/<id>/` (no secrets: API keys reach the relay only through its environment), sockets in `$XDG_RUNTIME_DIR/fuseraft-web/` or the temp directory. A relay's files go when its process ends.
- **Not covered:** a reboot, or killing the relays themselves, ends the sessions. A session can then be resumed from the Sessions page, and a chat from its snapshot.
