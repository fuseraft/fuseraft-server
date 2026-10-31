using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Server.Models;
using fuseraft.Server.Services.Cli;

namespace fuseraft.Server.Services;

/// <summary>
/// Runs orchestration sessions as <c>fuseraft run --bridge</c> child processes — one per session — and
/// turns their JSONL events into <see cref="SessionEvent"/>s for the Blazor pages. Past sessions come
/// from <c>fuseraft sessions --json</c>; their messages are fetched on demand with <c>--show</c>.
/// </summary>
public sealed class SessionHostService : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly FuseraftCli _cli;
    private readonly HitlBroker  _hitlBroker;
    private readonly ILogger<SessionHostService> _logger;

    internal static TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
    internal static TimeSpan CancelGrace    { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fired when a new session event arrives. Args: (sessionId, event).</summary>
    public event Action<string, SessionEvent>? EventFired;

    /// <summary>Fired when the session list changes (new session, status change, etc.).</summary>
    public event Action? SessionListChanged;

    /// <summary>Why the session history couldn't be loaded, if it couldn't.</summary>
    public string? HistoryError { get; private set; }

    internal Task HistoryLoad { get; }

    public SessionHostService(FuseraftCli cli, HitlBroker hitlBroker, ILogger<SessionHostService> logger)
    {
        _cli        = cli;
        _hitlBroker = hitlBroker;
        _logger     = logger;
        HistoryLoad = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var sessions = await _cli.ListAsync<WireSession>("sessions", "--all", "--json");
            foreach (var s in sessions.OrderByDescending(s => s.LastUpdatedAt).Take(100))
                _sessions.TryAdd(s.SessionId, FromHistory(s));
            SessionListChanged?.Invoke();
        }
        catch (Exception ex)
        {
            HistoryError = ex.Message;
            _logger.LogWarning(ex, "Could not load session history");
        }
    }

    private static ManagedSession FromHistory(WireSession s) => new()
    {
        SessionId        = s.SessionId,
        Task             = s.Task,
        ConfigPath       = s.ConfigPath ?? string.Empty,
        WorkingDirectory = s.WorkingDirectory,
        Status           = s.IsComplete ? SessionStatus.Succeeded : SessionStatus.Idle,
        StartedAt        = s.StartedAt,
        EndedAt          = s.IsComplete ? s.LastUpdatedAt : null,
        Succeeded        = s.IsComplete ? true : null,
    };

    public IEnumerable<ManagedSession> GetSessions() =>
        _sessions.Values.OrderByDescending(s => s.StartedAt);

    public ManagedSession? GetSession(string sessionId) =>
        _sessions.GetValueOrDefault(sessionId);

    /// <summary>
    /// Returns the session with its stored messages loaded, fetching them from fuseraft if it's from
    /// history (or was started outside this server). Null if fuseraft doesn't know it.
    /// </summary>
    public async Task<ManagedSession?> LoadSessionAsync(string sessionId)
    {
        var existing = GetSession(sessionId);
        if (existing is not null && (existing.HistoryLoaded || existing.Process is not null))
            return existing;

        WireSessionDetail detail;
        try
        {
            detail = await _cli.ItemAsync<WireSessionDetail>("sessions", "--show", sessionId, "--json");
        }
        catch (FuseraftCliException) when (existing is null)
        {
            return null;
        }

        var session = _sessions.GetOrAdd(sessionId, _ => FromHistory(new WireSession
        {
            SessionId = detail.SessionId, Task = detail.Task, WorkingDirectory = detail.WorkingDirectory,
            ConfigPath = detail.ConfigPath, StartedAt = detail.StartedAt, LastUpdatedAt = detail.LastUpdatedAt,
            IsComplete = detail.IsComplete,
        }));
        lock (session)
        {
            if (session.HistoryLoaded) return session;
            foreach (var msg in detail.Messages)
                session.AddEvent(new SessionEvent { Type = "message", Message = msg, Timestamp = msg.Timestamp });
            if (detail.IsComplete)
                session.AddEvent(new SessionEvent { Type = "session_end", Succeeded = true });
            session.HistoryLoaded = true;
        }
        return session;
    }

    public Task<string> StartSessionAsync(string task, string configPath, string workspaceDir = "")
    {
        var absConfig = Path.IsPathRooted(configPath)
            ? configPath
            : !string.IsNullOrEmpty(workspaceDir)
                ? Path.GetFullPath(Path.Combine(workspaceDir, configPath))
                : Path.GetFullPath(configPath);

        return LaunchAsync(["run", "--bridge", "-c", absConfig, task],
            string.IsNullOrWhiteSpace(workspaceDir) ? null : workspaceDir, existing: null);
    }

    public async Task<string> ResumeSessionAsync(string sessionId)
    {
        var existing = await LoadSessionAsync(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");
        if (existing.IsLive)
            throw new InvalidOperationException($"Session '{sessionId}' is already running.");
        if (existing.Status == SessionStatus.Succeeded)
            throw new InvalidOperationException($"Session '{sessionId}' is already complete.");

        var cwd = existing.WorkingDirectory is { Length: > 0 } wd && Directory.Exists(wd) ? wd : null;
        return await LaunchAsync(["run", "--bridge", "--resume", sessionId], cwd, existing);
    }

    /// <summary>
    /// Starts a bridge process and waits for its <c>ready</c> event, which carries the session ID.
    /// A run that fails before starting sends only <c>result</c>; that becomes the exception.
    /// </summary>
    private async Task<string> LaunchAsync(string[] args, string? cwd, ManagedSession? existing)
    {
        var proc = _cli.StartBridge(args, cwd);
        JsonElement ready;
        try
        {
            ready = await ReadReadyAsync(proc).WaitAsync(StartupTimeout);
        }
        catch (TimeoutException)
        {
            proc.Kill();
            proc.Dispose();
            throw new FuseraftCliException($"fuseraft didn't start the session within {StartupTimeout.TotalSeconds:0} seconds.");
        }
        catch
        {
            proc.Kill();
            proc.Dispose();
            throw;
        }

        var sessionId = ready.GetProperty("sessionId").GetString()!;
        var session = new ManagedSession
        {
            SessionId        = sessionId,
            Task             = Str(ready, "task") ?? existing?.Task ?? string.Empty,
            ConfigPath       = Str(ready, "config") ?? existing?.ConfigPath ?? string.Empty,
            WorkingDirectory = cwd ?? existing?.WorkingDirectory,
            Status           = SessionStatus.Running,
            StartedAt        = existing?.StartedAt ?? DateTimeOffset.UtcNow,
            Process          = proc,
            HistoryLoaded    = true,
        };
        foreach (var evt in existing?.SnapshotEvents().Where(e => e.Type != "session_end") ?? [])
            session.AddEvent(evt);

        _sessions[sessionId] = session;
        Emit(session, new SessionEvent { Type = "session_start" });
        SessionListChanged?.Invoke();

        _ = Task.Run(() => PumpAsync(session, proc));
        return sessionId;
    }

    private static async Task<JsonElement> ReadReadyAsync(IBridgeProcess proc)
    {
        while (await proc.Output.ReadLineAsync() is { } line)
        {
            if (TryParse(line) is not { } evt) continue;
            switch (Str(evt, "type"))
            {
                case "ready":
                    FuseraftCli.CheckVersion(evt);
                    return evt;
                case "result":
                    throw new FuseraftCliException(Str(evt, "errorMessage") ?? "fuseraft couldn't start the session.");
            }
        }

        var exit = await proc.WaitForExitAsync();
        throw new FuseraftCliException(
            $"fuseraft exited before the session started (exit {exit}). {proc.StderrTail.Split('\n').LastOrDefault()}".Trim());
    }

    private async Task PumpAsync(ManagedSession session, IBridgeProcess proc)
    {
        var relay  = session.Relay ??= new ApprovalRelay(_hitlBroker, proc.Input, ApprovalSource.Session);
        var result = default(JsonElement?);
        try
        {
            while (await proc.Output.ReadLineAsync() is { } line)
            {
                if (TryParse(line) is not { } evt) continue;
                switch (Str(evt, "type"))
                {
                    case "agent_starting":
                        Emit(session, new SessionEvent { Type = "agent_starting", AgentName = Str(evt, "agent") });
                        break;
                    case "tool_calling":
                        var args = Str(evt, "argsSummary");
                        Emit(session, new SessionEvent
                        {
                            Type = "tool_calling", AgentName = Str(evt, "agent"),
                            Detail = args is null ? Str(evt, "tool") : $"{Str(evt, "tool")}({args})",
                        });
                        break;
                    case "agent_message":
                        var message = evt.GetProperty("message").Deserialize<WireAgentMessage>(FuseraftCli.JsonOpts);
                        if (message is null) break;
                        Emit(session, new SessionEvent
                        {
                            Type    = "message",
                            Message = message,
                            Elapsed = evt.TryGetProperty("elapsedMs", out var ms) ? TimeSpan.FromMilliseconds(ms.GetInt64()) : null,
                        });
                        break;
                    case "approval_request":
                        relay.Handle(session.SessionId, evt);
                        break;
                    case "result":
                        result = evt;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lost the event stream of session {SessionId}", session.SessionId);
        }

        var exit = await proc.WaitForExitAsync();
        _hitlBroker.CancelSession(session.SessionId);
        session.Process = null;
        Finish(session, result, exit, proc.StderrTail);
        proc.Dispose();
    }

    private void Finish(ManagedSession session, JsonElement? result, int exit, string stderrTail)
    {
        var succeeded = result is { } r && r.TryGetProperty("succeeded", out var ok) && ok.ValueKind == JsonValueKind.True;
        session.EndedAt   = DateTimeOffset.UtcNow;
        session.Succeeded = succeeded;

        if (succeeded)
            session.Status = SessionStatus.Succeeded;
        else if (session.CancelRequested)
        {
            session.Status       = SessionStatus.Cancelled;
            session.ErrorMessage = "Cancelled by user.";
        }
        else
        {
            session.Status       = SessionStatus.Failed;
            session.ErrorMessage = result is { } failed
                ? Str(failed, "errorMessage") ?? $"The session failed (exit {exit})."
                : $"fuseraft exited unexpectedly (exit {exit}). {stderrTail.Split('\n').LastOrDefault()}".Trim();
        }

        Emit(session, new SessionEvent
        {
            Type = "session_end", Succeeded = succeeded, ErrorMessage = succeeded ? null : session.ErrorMessage,
        });
        SessionListChanged?.Invoke();
    }

    /// <summary>Asks the session to stop; kills its process if it hasn't exited after <see cref="CancelGrace"/>.</summary>
    public void CancelSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || s.Process is not { } proc) return;
        s.CancelRequested = true;
        _hitlBroker.CancelSession(sessionId);
        _ = (s.Relay ??= new ApprovalRelay(_hitlBroker, proc.Input, ApprovalSource.Session)).InterruptAsync();
        _ = KillIfStillRunningAsync(proc);
    }

    private static async Task KillIfStillRunningAsync(IBridgeProcess proc)
    {
        try { await proc.WaitForExitAsync().WaitAsync(CancelGrace); }
        catch (TimeoutException) { proc.Kill(); }
        catch { /* disposed: it exited */ }
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s) && s.IsLive)
            throw new InvalidOperationException("Cancel the session before deleting it.");

        await _cli.RunAsync("sessions", "--delete", sessionId);
        _sessions.TryRemove(sessionId, out _);
        SessionListChanged?.Invoke();
    }

    private void Emit(ManagedSession session, SessionEvent evt)
    {
        session.AddEvent(evt);
        EventFired?.Invoke(session.SessionId, evt);
    }

    private static JsonElement? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null; // a stray non-JSON line; the protocol says to ignore it
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
            s.Process?.Kill();
    }
}
