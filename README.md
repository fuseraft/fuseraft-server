# fuseraft-server

<img src="wwwroot/fuseraft-banner.png" alt="fuseraft — an agent orchestration framework">

A Blazor Server web UI for [fuseraft](https://github.com/fuseraft/fuseraft-cli) — manage agent orchestration sessions, build orchestration configs visually, handle human-in-the-loop approvals, and schedule recurring runs, all from a browser.

---

## Features

### Sessions
Start and monitor multi-agent orchestration sessions. Each session streams live agent events as they happen. Running sessions can be cancelled; completed sessions retain their full event history.

### Orchestration Builder
Build orchestration configs without touching YAML directly.

- Pick from 10 built-in templates (Dev Team, Research, DevOps, Minimal, and more)
- Edit agents visually: name, description, instructions, model override, endpoint, plugins, FunctionChoice, TrustScore, MaxTokens
- Select a stored **Model Profile** to populate model ID and endpoint in one click
- Preview and hand-edit the generated YAML side-by-side
- Save to a workspace directory and launch directly into a new session

### Model Profiles
Store model IDs, API endpoints, and API keys once — reference them from any orchestration.

- Add profiles for any provider (Anthropic, OpenAI, xAI, Google, Mistral, Ollama, …)
- API keys are stored in the OS keychain (GNOME Keyring on Linux via `secret-tool`, macOS Keychain via the `security` CLI, Windows Credential Manager via DPAPI) with a mode-600 plain-text fallback at `~/.fuseraft/profile-keys/`
- On startup, each profile's key is injected as `FUSERAFT_PROFILE_{id}_KEY` so the orchestrator resolves it automatically via `apiKeyEnvVar:` in the YAML
- Non-secret fields (name, model ID, endpoint, provider) are persisted to `~/.fuseraft/model-profiles.json`

### Plugins
Browse every registered plugin and its functions — name, description, and full function list — with a live search that filters across plugins and individual function descriptions.

### Context
Attach reference documents to a workspace. Imported files are indexed in `.fuseraft/context/` and injected into agent context at session start.

### Configs
Validate existing orchestration YAML files. The panel shows agent names, selection type, and any configuration issues (missing API key env vars, unknown selection strategies, etc.).

### Schedule
Define recurring orchestration runs on a cron schedule.

### HITL (Human-in-the-Loop)
Agents can pause and request human input mid-run. The HITL panel shows pending prompts with a badge count in the nav, lets you respond or redirect, and unblocks the waiting agent automatically.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `fuseraft-cli` (included as a project reference via `../fuseraft-cli/src/fuseraft.csproj`)
- Linux: `secret-tool` (from `libsecret-tools`) for keychain storage, or keys fall back to plain-text

---

## Running

```bash
dotnet run --project fuseraft-server/FuseraftServer.csproj
```

Then open [http://localhost:5000](http://localhost:5000).

The project ships with a `Properties/launchSettings.json` that sets `ASPNETCORE_ENVIRONMENT=Development`, which is required for Blazor's static web assets (including `blazor.web.js`) to be served correctly.

---

## License

MIT — see [LICENSE](LICENSE).
