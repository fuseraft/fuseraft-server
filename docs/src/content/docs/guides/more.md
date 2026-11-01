---
title: Profiles, schedule and more
description: Model profiles, scheduled jobs, plugins, context, configs and memory.
---

## Model profiles

A model profile stores a provider, a model ID, an endpoint, a reasoning effort and an API key once,
for every chat and session to use.

API keys go in the OS keychain: GNOME Keyring via `secret-tool`, the macOS Keychain or the Windows
Credential Manager. Without one, they go in a mode-600 file in your fuseraft home. A profile's key
reaches only the processes you start:

- as `FUSERAFT_PROFILE_{id}_KEY` for sessions, so `apiKeyEnvVar:` in a config resolves it;
- as `FUSERAFT_API_KEY` for REPL chats.

## Schedule

The **Schedule** page adds, enables, disables and removes `fuseraft schedule` jobs: a name, a cron
expression, the task, and optionally a config path, a working directory and a description.

fuseraft-web runs every account's due jobs once a minute, so no cron entry is needed. Turn that off
with [`Fuseraft:RunSchedules`](/fuseraft-web/reference/config/). **Run** starts a job now as a live
session, with approvals, and leaves its next scheduled run as it is.

## Plugins

Every plugin fuseraft's agents can use, with its functions.

## Context

Reference documents attached to a workspace for agents to read: a file or directory, with a name and
a description. See the fuseraft [context store docs](/fuseraft-cli/context-store/).

## Configs

Scan a directory for orchestration configs and validate them with `fuseraft validate`.

## Memory

Browse and delete the memories your chats and agents keep. See the fuseraft
[knowledge docs](/fuseraft-cli/knowledge/).
