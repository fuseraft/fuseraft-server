---
title: Accounts
description: Who can see what, each account's own fuseraft home, workspace roots, and managing accounts from the Users page or a shell.
---

Every page needs a signed-in account. People sign in with a username and password, with
[single sign-on](/fuseraft-web/guides/single-sign-on/), or with either.

## Each account has its own fuseraft home

Each account has its own `FUSERAFT_HOME`, so its sessions, REPL chats, memory, schedule and model
profiles are its own. Every fuseraft process an account starts runs against its home and gets only
that account's profile keys.

The first account gets fuseraft's default home, `~/.fuseraft`, so a machine that used fuseraft
before keeps its history. Later accounts get one under the
[data directory](/fuseraft-web/reference/config/).

## Users and admins

| | Users | Admins |
|---|---|---|
| Their own sessions, chats, memory, schedule, profiles | ✓ | ✓ |
| Other people's sessions and approvals | | see and act on them |
| Other people's REPL chats | | |
| Workspaces | only under their workspace roots | anywhere |
| Daemons | the ones an admin shares with them | all, and adds and connects them |
| Users page | | ✓ |

A non-admin's **workspace roots** bound the directories they can pick, run sessions and chats in,
and point configs at. By default that's `<data>/workspaces/<name>`
([`Auth:DefaultWorkspaceRoot`](/fuseraft-web/reference/config/)), and admins change each account's
roots on the Users page.

:::caution[Trust model]
Accounts separate people who trust each other, not strangers. Every agent runs as the server's OS
user, so an agent's shell or file tools can reach whatever that user can, including other accounts'
homes. Workspace roots bound what the UI lets someone pick, not what an agent can do once it runs.
For untrusted users, run one fuseraft-web per OS user or container.
:::

## Managing accounts

Admins manage accounts on the **Users** page: add them, reset passwords, promote or demote, disable,
remove, and edit workspace roots. Everyone can change their own password and link single sign-on on
the **Account** page.

The same works from a shell, including while the server runs. It's also how to recover a locked-out
admin. See [Command line](/fuseraft-web/reference/cli/).

```bash
fuseraft-web users add alice --admin     # prompts for a password (or set FUSERAFT_WEB_PASSWORD)
fuseraft-web users passwd alice
fuseraft-web users disable bob           # ends bob's sign-ins at once
```

## Passwords and sign-ins

- Passwords are at least 10 characters and stored as PBKDF2-SHA256 hashes.
- Sign-in is rate limited to 10 attempts a minute per address.
- Changing a password or disabling an account ends that account's sign-ins on its next request, and
  a role change applies from its next request.
- A sign-in lasts 12 hours without activity ([`Auth:SessionHours`](/fuseraft-web/reference/config/)).
- The last admin can't be demoted, disabled or removed.
