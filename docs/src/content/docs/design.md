---
title: How fuseraft-web works
description: The wire protocol, relay processes, per-account homes, the trust model, and how daemons are attached.
---

## It runs the CLI, not fuseraft's code

fuseraft-web doesn't link fuseraft's assemblies. It runs the `fuseraft` CLI and speaks its versioned
[wire protocol](/fuseraft-cli/protocol/):

- `--json` queries for lists: sessions, plugins, configs, memory, the schedule.
- JSONL event streams for live work: `fuseraft run --bridge` for sessions and
  `fuseraft repl --vscode` for REPL chats, the same bridge the VS Code extension uses.

So a fuseraft upgrade can't break fuseraft-web's build. fuseraft reports the protocol version it
speaks, and a mismatch is reported in the UI and the log rather than misread.

## Relays keep work alive

Each session and chat runs inside a small relay process, `fuseraft-web relay`, that holds the
fuseraft process, keeps a log of its events, and listens on a local socket. The server talks to the
relay, not to fuseraft directly.

When the server stops, the relays don't. The next server finds them, replays their logs to rebuild
each page, and asks again any approval still waiting. See
[Restarts don't end work](/fuseraft-web/deploy/restarts/).

## One fuseraft home per account

Every fuseraft process an account starts runs with that account's `FUSERAFT_HOME`, and gets only
that account's API keys, through its environment. So sessions, chats, memory, the schedule and model
profiles are kept apart per account, and fuseraft's own files are the record: a session started
from the terminal against the same home shows up in the browser.

## The trust model

Accounts separate people who trust each other. Every agent runs as the server's OS user, so its
shell and file tools can reach whatever that user can, including other accounts' homes. Workspace
roots bound what the UI lets someone pick, not what an agent can do once it runs. For untrusted
users, run one fuseraft-web per OS user or container.

## Daemons

A daemon is a `fuseraft serve` process on this machine or another, running its own queue in its own
fuseraft home. fuseraft-web attaches to its WebSocket as one client among many, with a token and a
client id of its own:

- The token decides what fuseraft-web may do there: `full` sends tasks and answers approvals,
  `read` only watches.
- The client id lets the daemon recognize fuseraft-web after a reconnect, so a pending approval is
  asked again and finished results are delivered instead of lost.
- fuseraft-web records who sent each task, so on a shared daemon each user sees and approves only
  their own.

The daemon, not fuseraft-web, owns the queue, so its tasks survive fuseraft-web going away
entirely. See [Daemons](/fuseraft-web/guides/daemons/).
