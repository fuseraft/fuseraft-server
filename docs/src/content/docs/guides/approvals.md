---
title: Approvals
description: The HITL page collects every approval your sessions, chats and daemon tasks are waiting on.
---

Agents ask before they act. The **HITL** (human-in-the-loop) page collects every approval your
sessions, REPL chats and daemon tasks are waiting on, with a badge count in the nav. Each one links
back to where it came from.

| Kind | What it shows |
|---|---|
| Shell | the command to run |
| File write | the path, and a diff of the change |
| Tool | the plugin and action, with its details |
| Handoff | which agent hands off to which, and the keyword that triggered it |

**Approve** lets it go ahead; **Deny** refuses it, and the agent is told so.

## Who sees which approvals

- Everyone sees the approvals for their own sessions, chats and daemon tasks.
- Admins also see, and can answer, other people's session approvals.
- REPL chat approvals are only ever their owner's.
- On a shared daemon, a non-admin sees only the approvals for the tasks they sent. See
  [Daemons](/fuseraft-web/guides/daemons/#who-can-use-a-daemon).

## Approvals survive restarts

An approval that's waiting when fuseraft-web restarts is asked again when the server comes back,
for sessions and chats (see [Restarts](/fuseraft-web/deploy/restarts/)) and for daemon tasks (see
[Daemons](/fuseraft-web/guides/daemons/#restarts-on-either-side)). A session that needs an approval
just waits until someone answers it.
