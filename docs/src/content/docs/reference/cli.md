---
title: Command line
description: Running the server, and managing accounts with fuseraft-web users.
---

## The server

```bash
fuseraft-web
```

With no arguments, `fuseraft-web` runs the server, configured by `appsettings.json` and the
environment (see [Configuration](/fuseraft-web/reference/config/)). On a fresh install it logs a
one-time link to create the first admin.

## `fuseraft-web users`

Manage accounts from a shell. It reads the same `appsettings.json` and environment as the server, so
it works on the same data directory, and works while the server runs. Run it as the service's OS user
(`sudo -u fuseraft-web …`) when fuseraft-web [runs as a service](/fuseraft-web/deploy/service/).

```bash
fuseraft-web users list
fuseraft-web users add <name> [--admin]     # prompts for a password
fuseraft-web users passwd <name>            # prompts for a new password
fuseraft-web users admin <name> on|off
fuseraft-web users disable <name>
fuseraft-web users enable <name>
fuseraft-web users remove <name>
```

| Command | |
|---|---|
| `list` | Every account, its role, and whether it's disabled |
| `add` | Create an account; `--admin` makes it an admin |
| `passwd` | Set a new password, and end the account's sign-ins |
| `admin` | Promote or demote |
| `disable` | Block sign-in and end the account's sign-ins at once |
| `enable` | Allow sign-in again |
| `remove` | Delete the account. Its fuseraft home stays on disk |

Set `FUSERAFT_WEB_PASSWORD` to skip the password prompt. Passwords are at least 10 characters. The
last admin can't be demoted, disabled or removed.

This is also how to recover a locked-out admin: `passwd` them, or `add` a new admin.

## `fuseraft-web relay`

Used internally: fuseraft-web starts one relay per session and REPL chat, so they outlive the
server. You don't run it yourself. See [Restarts don't end work](/fuseraft-web/deploy/restarts/).
