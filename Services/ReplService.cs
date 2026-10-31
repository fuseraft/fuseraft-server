using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

public enum ReplStatus { Idle, Thinking, Error }

public sealed record ReplMessage(string Role, string Text, IReadOnlyList<string> ToolCalls, DateTimeOffset At);

public sealed class ActiveReplSession : IDisposable
{
    /// <summary>Stable dictionary key assigned at creation; never changes.</summary>
    public string        Key       { get; init; } = "";
    /// <summary>Real session ID from the CLI's 'ready' event; starts equal to Key.</summary>
    public string        SessionId { get; set; }  = "";
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

    public void Dispose()
    {
        Bridge?.Kill();
        Bridge?.Dispose();
    }
}

/// <summary>
/// Manages fuseraft REPL sessions by spawning <c>fuseraft repl --vscode</c> as a child
/// process and speaking the same JSON-line protocol used by the VS Code extension. The REPL
/// asks before writing files or running commands; those requests go to the <see cref="HitlBroker"/>.
/// </summary>
public sealed class ReplService : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveReplSession> _sessions = new();
    private readonly ModelProfileService _profiles;
    private readonly FuseraftCli _cli;
    private readonly HitlBroker  _hitl;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public event Action<string>? SessionChanged;

    public ReplService(ModelProfileService profiles, FuseraftCli cli, HitlBroker hitl)
    {
        _profiles = profiles;
        _cli      = cli;
        _hitl     = hitl;
    }

    // ── Session creation ─────────────────────────────────────────────────────

    public async Task<string> CreateSessionAsync(string profileId, string systemPrompt, string? workspaceDir = null)
    {
        var profile = await GetProfileAsync(profileId);
        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = key,
            ProfileId = profile.Id,
            ModelId   = profile.ModelId,
            Cwd       = string.IsNullOrWhiteSpace(workspaceDir) ? null : workspaceDir.Trim(),
        };
        _sessions[key] = session;

        var args = new List<string> { "repl", "--vscode", "--no-banner", "--model", profile.ModelId };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            args.AddRange(["--system", systemPrompt]);

        Spawn(key, session, profile, args);
        return key;
    }

    public async Task<string> ResumeSnapshotAsync(string snapshotSessionId)
    {
        var snapshot = (await ListSnapshotsAsync()).FirstOrDefault(s => s.SessionId == snapshotSessionId)
            ?? throw new InvalidOperationException($"Snapshot '{snapshotSessionId}' not found.");

        var profiles = await _profiles.ListAsync();
        var profile  = profiles.FirstOrDefault(p => p.ModelId == snapshot.ModelId)
                    ?? profiles.FirstOrDefault()
                    ?? throw new InvalidOperationException("No model profiles configured.");

        var key     = NewKey();
        var session = new ActiveReplSession
        {
            Key       = key,
            SessionId = snapshotSessionId,
            ProfileId = profile.Id,
            ModelId   = snapshot.ModelId,
            // A chat resumes in the directory it ran in, so its sandbox and paths still line up.
            Cwd       = Directory.Exists(snapshot.Cwd) ? snapshot.Cwd : null,
        };
        _sessions[key] = session;

        var args = new List<string>
        {
            "repl", "--vscode", "--no-banner",
            "--model", snapshot.ModelId,
            "--resume", snapshotSessionId,
        };
        Spawn(key, session, profile, args);
        return key;
    }

    private void Spawn(string key, ActiveReplSession session, ModelProfile profile, List<string> args)
    {
        // Inject the profile's API key as FUSERAFT_API_KEY — same channel the VS Code
        // extension uses so the CLI picks it up in --vscode mode.
        var env    = new Dictionary<string, string>();
        var apiKey = Environment.GetEnvironmentVariable(profile.ApiKeyEnvVar) ?? "";
        if (!string.IsNullOrEmpty(apiKey))
            env["FUSERAFT_API_KEY"] = apiKey;

        try
        {
            var bridge = _cli.StartBridge(args, session.Cwd, env);
            session.Bridge = bridge;
            session.Relay  = new ApprovalRelay(_hitl, bridge.Input, ApprovalSource.Repl);
            _ = Task.Run(() => ReadLoopAsync(key, bridge));
        }
        catch (Exception ex)
        {
            session.Status = ReplStatus.Error;
            session.Error  = $"Could not start fuseraft: {ex.Message}";
            SessionChanged?.Invoke(key);
        }
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
            if (_sessions.TryGetValue(key, out var s))
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

    public void SendMessage(string key, string text)
    {
        if (!_sessions.TryGetValue(key, out var s)) return;
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

    public void ClearSession(string key)
    {
        if (!_sessions.TryGetValue(key, out var s)) return;
        s.Messages.Clear();
        SessionChanged?.Invoke(key);
        // Also clear CLI history
        WriteStdinRaw(s, "/clear");
    }

    /// <summary>Stops the reply in progress; the chat stays open.</summary>
    public void CancelSession(string key)
    {
        if (!_sessions.TryGetValue(key, out var s) || s.Relay is not { } relay) return;
        _hitl.CancelSession(s.SessionId);
        _ = relay.InterruptAsync();
    }

    public void CloseSession(string key)
    {
        if (_sessions.TryRemove(key, out var s))
        {
            s.Dispose();
            SessionChanged?.Invoke(key);
        }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public ActiveReplSession? GetSession(string key) => _sessions.GetValueOrDefault(key);

    public IEnumerable<ActiveReplSession> GetSessions() =>
        _sessions.Values.OrderByDescending(s => s.StartedAt);

    public Task<List<WireReplSession>> ListSnapshotsAsync() =>
        _cli.ListAsync<WireReplSession>("sessions", "--repl", "--json");

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

    private async Task<ModelProfile> GetProfileAsync(string profileId)
    {
        var profiles = await _profiles.ListAsync();
        return profiles.FirstOrDefault(p => p.Id == profileId)
            ?? throw new InvalidOperationException($"Profile '{profileId}' not found.");
    }

    private static string NewKey() => Guid.NewGuid().ToString("N")[..8];

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() : null;

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }
}
