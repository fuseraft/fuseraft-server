---
title: Quickstart
description: Start fuseraft-web, create the first admin, add a model profile, and run your first chat and session.
---

This gets fuseraft-web running on your own machine, reachable only from it. To serve a team, read
[Run it as a service](/fuseraft-web/deploy/service/) afterwards.

## 1. Start it

After you [install](/fuseraft-web/start/install/) fuseraft-web and the `fuseraft` CLI, run it:

```bash
./fuseraft-web
```

It listens on `http://localhost:5000`. On a fresh install the log prints a one-time setup link:

```
There are no accounts yet. Create the first admin at http://localhost:5000/setup?code=…
```

## 2. Create the first admin

Open the link and pick a username and password (at least 10 characters). Or create the admin from a
shell, which works while the server runs:

```bash
./fuseraft-web users add alice --admin
```

The first account gets fuseraft's default home, `~/.fuseraft`, so if you used fuseraft on this
machine before, your sessions, chats and memory are already there.

## 3. Add a model profile

Open **Model Profiles** and add one: a model ID, an endpoint if your provider needs one, and an API
key. The key goes in the OS keychain, and reaches only the fuseraft processes you start.

## 4. Chat with the REPL

Open **REPL**, pick your profile and a workspace directory, and send a message. Replies stream in.
When the agent wants to write a file or run a command, the chat asks you first. See
[REPL](/fuseraft-web/guides/repl/).

## 5. Run a session

Open **Orchestration Builder**, pick a team template and a workspace, and generate the config. Launch
it, type the task for the team, and start the session. It opens on its own page, with each agent's
turns and tool calls streaming in and its approvals waiting there and on the **HITL** page. See
[Sessions](/fuseraft-web/guides/sessions/).

## Next

- [Add accounts](/fuseraft-web/guides/accounts/) for the rest of the team, or set up
  [single sign-on](/fuseraft-web/guides/single-sign-on/).
- [Attach to a daemon](/fuseraft-web/guides/daemons/) running on another machine.
- [Run it as a service](/fuseraft-web/deploy/service/) behind a
  [TLS reverse proxy](/fuseraft-web/deploy/reverse-proxy/).
