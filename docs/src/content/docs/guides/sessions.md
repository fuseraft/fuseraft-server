---
title: Sessions
description: Start, watch, cancel and resume multi-agent orchestration sessions, and generate configs with the Orchestration Builder.
---

A session is one run of a fuseraft orchestration: a team of agents working a task under a config.
Each one is its own `fuseraft run --bridge` process, so several can run at once.

## Start a session

On **Sessions** (or the Dashboard), click **New Session** and fill in:

- **Task**: what the team should do.
- **Workspace**: the directory the agents work in. Non-admins can only pick one under their
  [workspace roots](/fuseraft-web/guides/accounts/#users-and-admins).
- **Config File**: the orchestration config, relative to the workspace or absolute. It defaults to
  `.fuseraft/config/orchestration.yaml`.

The session opens on its own page.

## Watch it

Agent turns and tool calls stream in as they happen. Commands, file writes and agent handoffs wait
for your approval, on the session's page and on the [HITL](/fuseraft-web/guides/approvals/) page.

The Sessions list filters by status: running, succeeded, failed or cancelled. It includes past
sessions in your fuseraft home, including ones started from the terminal, with their full message
history.

## Cancel and resume

**Cancel Session** stops a running session. **Resume Session** picks up a cancelled, failed or
interrupted one from its last checkpoint.

Sessions run in [relay processes](/fuseraft-web/deploy/restarts/) that outlive the server, so
restarting fuseraft-web doesn't interrupt them.

## Orchestration Builder

The **Orchestration Builder** generates a config from one of `fuseraft init`'s team templates and
launches it:

1. Pick a template. Orchestrations you've run before are listed at the top.
2. Pick a workspace and an output path for the config, and optionally a model (your
   [model profiles](/fuseraft-web/guides/more/#model-profiles) are suggested) and an endpoint.
3. **Generate** writes the config and shows it with its validation result.
4. **Start Session** opens the New Session dialog with the workspace and config filled in. Add the
   task and start it.

Edit the generated file like any other fuseraft config. See the fuseraft
[configuration docs](/fuseraft-cli/configuration/).
