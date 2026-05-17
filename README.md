# fuseraft-server

A Blazor Server web UI for [fuseraft](https://github.com/fuseraft/fuseraft-cli) — manage agent orchestration sessions, build orchestration configs visually, handle human-in-the-loop approvals, and schedule recurring runs, all from a browser.

---

## Features

### Sessions
Start and monitor multi-agent orchestration sessions. Each session streams live agent events as they happen. Running sessions can be cancelled; completed sessions retain their full event history.

### Orchestration Builder
Build orchestration configs without touching YAML directly.

- Pick from 10 built-in templates (Dev Team, Research, DevOps, Minimal, and more)
- Edit agents visually: name, description, instructions, model override, plugins, FunctionChoice, TrustScore, MaxTokens
- Preview and hand-edit the generated YAML side-by-side
- Save to a workspace directory and launch directly into a new session

### Plugins
Browse every registered plugin and its functions — name, description, and full function list — with a live search that filters across plugins and individual function descriptions.

### Schedule
Define recurring orchestration runs on a cron schedule.

### HITL (Human-in-the-Loop)
Agents can pause and request human input mid-run. The HITL panel shows pending prompts with a badge count in the nav, lets you respond or redirect, and unblocks the waiting agent automatically.

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- `fuseraft-cli` (included as a project reference via `../fuseraft-cli/src/FuseraftCli.csproj`)

---

## Running

```bash
dotnet run --project fuseraft-server/FuseraftServer.csproj
```

Then open [http://localhost:5000](http://localhost:5000).

The project ships with a `Properties/launchSettings.json` that sets `ASPNETCORE_ENVIRONMENT=Development`, which is required for Blazor's static web assets (including `blazor.web.js`) to be served correctly.

---

## Project structure

```
fuseraft-server/
  Components/
    Layout/        # MainLayout, NavMenu
    Pages/         # Dashboard, Sessions, SessionDetail, OrchestrationBuilder, Plugins, Schedule, Hitl
    Shared/        # DirPicker, LiveFeed, SessionTable, StatusBadge, AgentCard, ...
  Services/
    SessionHostService.cs         # Runs fuseraft-cli sessions in-process
    HitlBroker.cs                 # Manages pending HITL requests
    ScheduleService.cs            # Cron-based session scheduler
    OrchestrationTemplateService.cs # Template metadata + YAML generation
    PluginCatalogService.cs       # Reflects plugin functions from PluginRegistry
  wwwroot/
    app.css        # All styles (dark theme, component styles)
    fuseraft.svg   # Logo
```

---

## License

MIT — see [LICENSE](LICENSE).
