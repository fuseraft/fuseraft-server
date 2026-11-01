---
title: REPL
description: Chat with the fuseraft REPL in the browser, confined to a workspace, with approvals and saved chats.
---

The **REPL** page is the same engine as `fuseraft repl` and the VS Code extension, in a browser. Each
chat is a `fuseraft repl --vscode` process speaking the JSONL bridge.

## Start a chat

Pick a **Model Profile** and a **Workspace**, and optionally a **System Prompt**, then start the
chat. Type a message and press **Ctrl+Enter** to send it. Replies stream in and render as Markdown.

## The workspace and approvals

The chat's tools are confined to its workspace. When the agent wants to write a file or run a
command, the chat asks first, with the command or a diff of the file, and the request also appears
on the [HITL](/fuseraft-web/guides/approvals/) page. **Stop** ends the current turn.

## Slash commands

Slash commands such as `/model` or `/compact` go to the CLI, which handles them as it does in the
terminal. See the fuseraft [REPL docs](/fuseraft-cli/repl/) for the list.

## Saved chats

A chat is saved to your fuseraft home after each turn. **Recent Sessions** on the REPL page lists
them, and opening one resumes it. Your chats are your own: admins can't see them.

Open chats run in [relay processes](/fuseraft-web/deploy/restarts/), so restarting fuseraft-web
doesn't end them.
