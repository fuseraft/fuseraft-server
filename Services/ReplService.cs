using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Relay;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

public enum ReplStatus { Idle, Thinking, Error }

public sealed record ReplMessage(string Role, string Text, IReadOnlyList<string> ToolCalls, DateTimeOffset At);

public sealed class ActiveReplSession : IDisposable
{
    /// <summary>Stable dictionary key assigned at creation; never changes.</summary>
    public string        Key       { get; init; } = "";
    /// <summary>Real session ID from the CLI's 'ready' event; starts equal to Key.</summary>
    public string        SessionId { get; set; }  = "";
    public string        OwnerId   { get; init; } = "";
    public string        ProfileId { get; init; } = "";
    public string        ModelId   { get; set; }  = "";
    /// <summary>The directory the chat runs in; its tools are sandboxed to it.</summary>
    public string?       Cwd       { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public ReplStatus Status         { get; set; } = ReplStatus.Idle;
    public string?    StreamingText  { get; set; }
    public string?    StreamingTools { get; set; }
    public string?    Error          { get; set; }

    public List<ReplMessage> Messages { get; } = [];

    internal IBridgeProcess? Bridge { get; set; }
    internal ApprovalRelay?  Relay  { get; set; }

    /// <summary>Ends the chat's process; once it has exited, its relay's files go too.</summary>
    public void Dispose()
    {
        if (Bridge is not { } bridge) return;
        bridge.Kill();
        _ = Task.Run(async () =>
        {
            try { await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch { }
            bridge.Dispose();
        });
    }
}

/// <summary>
/// Manages fuseraft REPL sessions by spawning <c>fuseraft repl --vscode</c> as a child
/// process and speaking the same JSON-line protocol used by the VS Code extension. The REPL
/// asks before writing files or running commands; those requests go to the <see cref="HitlBroker"/>.
/// Each chat belongs to the account that opened it and runs against that account's fuseraft home. Chats run in
/// relays, so stopping the server leaves them open and the next server adopts them.
/// </summary>
public sealed class ReplService : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveReplSession> _sessions = new();
    private readonly ModelProfileRegistry _profiles;
    private readonly FuseraftCli _cli;
    private readonly HitlBroker  _hitl;
    private volatile bool _stopping;

    /// <summary>The <see cref="BridgeTag.Kind"/> of a chat's relay.</summary>
    public const string Kind = "repl";

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<string>? SessionChanged;

    public ReplService(ModelProfileRegistry profiles, FuseraftCli cli, HitlBroker hitl)
    {
        _profiles = profiles;
        _cli      = cli;
        _hitl     = hitl;
    }

    // ── Session creation ─────────────────────────────────────────────────────

    public async Task<string> CreateSessionAsync(WebUser user, string profileId, string systemPrompt, string? workspaceDir = null)
    {
        var access  = WorkspaceAccess.For(user);
        var cwd     = string.IsNullOrWhiteSpace(workspaceDir) ? null : access.Check(workspaceDir);
        if (cwd is null && access.Roots.Count > 0)
            throw new ArgumentException("Pick a workspace for the chat.");
        var profile = await GetProfileAsync(user, profileId);
        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = key,
            OwnerId   = user.Id,
            ProfileId = profile.Id,
            ModelId   = profile.ModelId,
            Cwd       = cwd,
        };
        _sessions[key] = session;

        var args = new List<string> { "repl", "--vscode", "--no-banner", "--model", profile.ModelId };
        args.AddRange(ConnectionArgs(profile));
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            args.AddRange(["--system", systemPrompt]);

        await SpawnAsync(user, key, session, profile, args);
        return key;
    }

    public async Task<string> ResumeSnapshotAsync(WebUser user, string snapshotSessionId)
    {
        var snapshot = (await ListSnapshotsAsync(user)).FirstOrDefault(s => s.SessionId == snapshotSessionId)
            ?? throw new InvalidOperationException($"Snapshot '{snapshotSessionId}' not found.");
        var cwd = Directory.Exists(snapshot.Cwd) ? snapshot.Cwd : null;
        if (cwd is not null && !WorkspaceAccess.For(user).Allows(cwd))
            throw new UnauthorizedAccessException($"That chat ran in '{cwd}', which is outside your workspaces.");

        var profiles = await _profiles.For(user).ListAsync();
        var profile  = profiles.FirstOrDefault(p => p.ModelId == snapshot.ModelId)
                    ?? profiles.FirstOrDefault()
                    ?? throw new InvalidOperationException("No model profiles configured.");

        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = snapshotSessionId,
            OwnerId   = user.Id,
            ProfileId = profile.Id,
            ModelId   = snapshot.ModelId,
            // A chat resumes in the directory it ran in, so its sandbox and paths still line up.
            Cwd       = cwd,
        };
        _sessions[key] = session;

        var args = new List<string>
        {
            "repl", "--vscode", "--no-banner",
            "--model", snapshot.ModelId,
            "--resume", snapshotSessionId,
        };
        args.AddRange(ConnectionArgs(profile));
        await SpawnAsync(user, key, session, profile, args);
        return key;
    }

    private async Task SpawnAsync(WebUser user, string key, ActiveReplSession session, ModelProfile profile, List<string> args)
    {
        // Inject the profile's API key as FUSERAFT_API_KEY — same channel the VS Code
        // extension uses so the CLI picks it up in --vscode mode.
        var env = new Dictionary<string, string>();
        if (await _profiles.For(user).GetKeyAsync(profile.Id) is { Length: > 0 } apiKey)
            env["FUSERAFT_API_KEY"] = apiKey;

        var tag = new BridgeTag
        {
            Kind = Kind, OwnerId = user.Id, StartedAt = session.StartedAt,
            Extra = { ["key"] = key, ["profileId"] = profile.Id, ["modelId"] = session.ModelId, ["sessionId"] = session.SessionId },
        };
        if (session.Cwd is not null) tag.Extra["cwd"] = session.Cwd;

        try
        {
            var bridge = await _cli.ForHome(user.Home).StartBridgeAsync(args, session.Cwd, env, tag);
            Attach(key, session, bridge);
        }
        catch (Exception ex)
        {
            session.Status = ReplStatus.Error;
            session.Error  = $"Could not start fuseraft: {ex.Message}";
            SessionChanged?.Invoke(key);
        }
    }

    private void Attach(string key, ActiveReplSession session, IBridgeProcess bridge)
    {
        session.Bridge = bridge;
        session.Relay  = new ApprovalRelay(_hitl, bridge.Input, ApprovalSource.Repl, ownerId: session.OwnerId);
        _ = Task.Run(() => ReadLoopAsync(key, bridge));
    }

    /// <summary>
    /// Takes over a chat a previous server left open. Its relay replays the conversation, including what was typed,
    /// so the chat reappears as it was, and approvals still waiting are asked again.
    /// </summary>
    public void AdoptRunning(BridgeTag tag, IBridgeProcess bridge, WebUser? owner)
    {
        if (owner is null || tag.Extra.GetValueOrDefault("key") is not { Length: > 0 } key)
        {
            bridge.Kill();
            bridge.Dispose();
            return;
        }
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = tag.Extra.GetValueOrDefault("sessionId") ?? key,
            OwnerId   = owner.Id,
            ProfileId = tag.Extra.GetValueOrDefault("profileId") ?? "",
            ModelId   = tag.Extra.GetValueOrDefault("modelId") ?? "",
            Cwd       = tag.Extra.GetValueOrDefault("cwd"),
            StartedAt = tag.StartedAt,
        };
        _sessions[key] = session;
        Attach(key, session, bridge);
        SessionChanged?.Invoke(key);
    }

    // ── Stdout reader ────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(string key, IBridgeProcess bridge)
    {
        try
        {
            while (await bridge.Output.ReadLineAsync() is { } raw)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var t))
                        HandleEvent(key, t.GetString() ?? "", root);
                }
                catch { /* non-JSON diagnostic line — ignore */ }
            }
        }
        catch { /* pipe closed */ }
        finally
        {
            if (_stopping)
            {
                // The server let go; the chat stays open in its relay.
            }
            else if (!_sessions.TryGetValue(key, out var s))
            {
                // Ended normally (session_end); once the process is gone its relay's files can go.
                await bridge.WaitForExitAsync();
                bridge.Dispose();
            }
            else
            {
                // Still listed means no session_end arrived: the process died. Keep the chat on
                // screen with the reason instead of silently dropping back to the start form.
                _hitl.CancelSession(s.SessionId);
                var exit = await bridge.WaitForExitAsync();
                s.Status         = ReplStatus.Error;
                s.Error          = $"fuseraft exited unexpectedly (exit {exit}). {bridge.StderrTail.Split('\n').LastOrDefault()}".Trim();
                s.StreamingText  = null;
                s.StreamingTools = null;
            }
            SessionChanged?.Invoke(key);
        }
    }

    // ── Event dispatcher ─────────────────────────────────────────────────────

    private void HandleEvent(string key, string type, JsonElement root)
    {
        if (!_sessions.TryGetValue(key, out var s)) return;

        switch (type)
        {
            case "ready":
                try
                {
                    FuseraftCli.CheckVersion(root);
                }
                catch (FuseraftCliException ex)
                {
                    s.Status = ReplStatus.Error;
                    s.Error  = ex.Message;
                    s.Bridge?.Kill();
                    break;
                }
                s.SessionId = Str(root, "sessionId") ?? s.SessionId;
                s.ModelId   = Str(root, "model")     ?? s.ModelId;
                s.Status    = ReplStatus.Idle;
                break;

            case "approval_request":
                s.Relay?.Handle(s.SessionId, root);
                break;

            case RelayBridge.ReplayedInput:
                // Something typed before this server took over, replayed so the chat reads as it did.
                var typed = Str(root, "text") ?? "";
                var raw   = root.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.True;
                if (raw && typed == "/clear")
                    s.Messages.Clear();
                else if (!raw)
                {
                    s.Messages.Add(new ReplMessage("user", typed, [], DateTimeOffset.UtcNow));
                    s.Status        = ReplStatus.Thinking;
                    s.StreamingText = "";
                }
                break;

            case "text":
            case "info":
                // Slash-command output, pre-rendered as Markdown.
                if (Str(root, "text") is { Length: > 0 } output)
                    s.Messages.Add(new ReplMessage("system", output, [], DateTimeOffset.UtcNow));
                break;

            case "token":
                s.StreamingText = (s.StreamingText ?? "") + (Str(root, "text") ?? "");
                s.Status = ReplStatus.Thinking;
                break;

            case "tool_call":
                var name = Str(root, "name") ?? "";
                s.StreamingTools = s.StreamingTools is null ? name : s.StreamingTools + " → " + name;
                break;

            case "message_end":
                if (s.StreamingText is { Length: > 0 } text)
                {
                    var tools = new List<string>();
                    if (root.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                        foreach (var item in tc.EnumerateArray())
                            if (item.GetString() is { } tn) tools.Add(tn);
                    s.Messages.Add(new ReplMessage("assistant", text, tools, DateTimeOffset.UtcNow));
                }
                s.Status        = ReplStatus.Idle;
                s.StreamingText = null;
                s.StreamingTools = null;
                break;

            case "cancelled":
                if (s.StreamingText is { Length: > 0 } partial)
                    s.Messages.Add(new ReplMessage("assistant", partial + "\n*(cancelled)*", [], DateTimeOffset.UtcNow));
                s.Status        = ReplStatus.Idle;
                s.StreamingText = null;
                s.StreamingTools = null;
                break;

            case "error":
                var err = Str(root, "text") ?? "Unknown error";
                s.Status        = ReplStatus.Error;
                s.Error         = err;
                s.StreamingText = null;
                s.StreamingTools = null;
                s.Messages.Add(new ReplMessage("system", $"Error: {err}", [], DateTimeOffset.UtcNow));
                break;

            case "warning":
                if (Str(root, "text") is { } warn)
                    s.Messages.Add(new ReplMessage("system", warn, [], DateTimeOffset.UtcNow));
                break;

            case "retrying":
                var att = root.TryGetProperty("attempt", out var ae) ? ae.GetInt32() : 0;
                var max = root.TryGetProperty("max",     out var me) ? me.GetInt32() : 0;
                s.Messages.Add(new ReplMessage("system", $"Retrying… ({att}/{max})", [], DateTimeOffset.UtcNow));
                break;

            case "plan":
                var planLines = new List<string>();
                if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                    foreach (var step in stepsEl.EnumerateArray())
                    {
                        var n    = step.TryGetProperty("step",        out var sn) ? sn.GetInt32()   : 0;
                        var desc = step.TryGetProperty("description", out var sd) ? sd.GetString() ?? "" : "";
                        var tool = step.TryGetProperty("tool",        out var st) ? st.GetString()       : null;
                        planLines.Add($"{n}. {desc}" + (tool is not null ? $"  `{tool}`" : ""));
                    }
                s.Messages.Add(new ReplMessage("system",
                    $"**Plan captured** ({planLines.Count} steps). Type `/execute` to run.\n\n" +
                    string.Join("\n", planLines),
                    [], DateTimeOffset.UtcNow));
                break;

            case "step_status":
                var sn2    = root.TryGetProperty("step",      out var ss2) ? ss2.GetInt32()    : 0;
                var stot   = root.TryGetProperty("total",     out var st2) ? st2.GetInt32()    : 0;
                var sstat  = root.TryGetProperty("status",    out var ss3) ? ss3.GetString() ?? "" : "";
                var sleft  = root.TryGetProperty("stepsLeft", out var sl)  ? sl.GetInt32()     : 0;
                var icon   = sstat == "complete" ? "✓" : sstat == "skipped" ? "↷" : "✗";
                var leftStr = sleft > 0 ? $" · {sleft} remaining" : "";
                s.Messages.Add(new ReplMessage("system", $"{icon} Step {sn2}/{stot} {sstat}{leftStr}", [], DateTimeOffset.UtcNow));
                break;

            case "session_end":
                s.Status        = ReplStatus.Idle;
                s.StreamingText = null;
                s.StreamingTools = null;
                s.Messages.Add(new ReplMessage("system", "Session ended.", [], DateTimeOffset.UtcNow));
                _sessions.TryRemove(key, out _);
                break;
        }

        SessionChanged?.Invoke(key);
    }

    // ── User actions ─────────────────────────────────────────────────────────

    public void SendMessage(WebUser user, string key, string text)
    {
        if (GetSession(user, key) is not { } s) return;
        if (s.Status == ReplStatus.Thinking) return;

        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        s.Messages.Add(new ReplMessage("user", trimmed, [], DateTimeOffset.UtcNow));
        s.Status        = ReplStatus.Thinking;
        s.StreamingText = "";
        s.StreamingTools = null;
        s.Error         = null;

        SessionChanged?.Invoke(key);
        WriteStdin(s, trimmed);
    }

    public void ClearSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not { } s) return;
        s.Messages.Clear();
        SessionChanged?.Invoke(key);
        // Also clear CLI history
        WriteStdinRaw(s, "/clear");
    }

    /// <summary>Stops the reply in progress; the chat stays open.</summary>
    public void CancelSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not { Relay: { } relay } s) return;
        _hitl.CancelSession(s.SessionId);
        _ = relay.InterruptAsync();
    }

    public void CloseSession(WebUser user, string key)
    {
        if (GetSession(user, key) is not null && _sessions.TryRemove(key, out var s))
        {
            s.Dispose();
            SessionChanged?.Invoke(key);
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>A chat <paramref name="user"/> may see and type into: only their own, admins included.</summary>
    public ActiveReplSession? GetSession(WebUser user, string key) =>
        _sessions.GetValueOrDefault(key) is { } s && s.OwnerId == user.Id ? s : null;

    public IEnumerable<ActiveReplSession> GetSessions(WebUser user) =>
        _sessions.Values.Where(s => s.OwnerId == user.Id).OrderByDescending(s => s.StartedAt);

    public Task<List<WireReplSession>> ListSnapshotsAsync(WebUser user) =>
        _cli.ForHome(user.Home).ListAsync<WireReplSession>("sessions", "--repl", "--json");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void WriteStdin(ActiveReplSession s, string text)
    {
        var json = JsonSerializer.Serialize(new { type = "user_input", text }, _jsonOpts);
        try { s.Bridge?.Input.WriteLine(json); } catch { }
    }

    private void WriteStdinRaw(ActiveReplSession s, string text)
    {
        // Non-JSON path: the CLI's ReadInput falls back to the raw string when
        // the line cannot be parsed as JSON.
        try { s.Bridge?.Input.WriteLine(text); } catch { }
    }

    private async Task<ModelProfile> GetProfileAsync(WebUser user, string profileId)
    {
        var profiles = await _profiles.For(user).ListAsync();
        return profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"Profile '{profileId}' not found.");
    }

    /// <summary>
    /// The profile's connection, for this chat only: without it the REPL uses the connection in the account's
    /// config, if it has one, whatever profile was picked.
    /// </summary>
    internal static IEnumerable<string> ConnectionArgs(ModelProfile profile)
    {
        // The Profiles page's "other" means OpenAI-compatible, which is the CLI's default for a custom endpoint.
        if (profile.Provider is { Length: > 0 } provider && provider != "other")
        {
            yield return "--provider";
            yield return provider;
        }
        if (!string.IsNullOrWhiteSpace(profile.Endpoint))
        {
            yield return "--endpoint";
            yield return profile.Endpoint.Trim();
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N")[..8];

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    /// <summary>Lets go of every chat; their relays keep them open for the next server.</summary>
    public void Dispose()
    {
        _stopping = true;
        foreach (var s in _sessions.Values) s.Bridge?.Dispose();
        _sessions.Clear();
    }
}
