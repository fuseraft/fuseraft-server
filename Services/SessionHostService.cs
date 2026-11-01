using System.Collections.Concurrent;
using System.Text.Json;
using fuseraft.Web.Models;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Relay;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services;

/// <summary>
/// Runs orchestration sessions as <c>fuseraft run --bridge</c> child processes — one per session — and
/// turns their JSONL events into <see cref="SessionEvent"/>s for the Blazor pages. Past sessions come
/// from <c>fuseraft sessions --json</c> in each user's fuseraft home; their messages are fetched on demand
/// with <c>--show</c>. Every session belongs to the account that started it (<see cref="WebUser.CanAccess"/>).
/// Sessions run in relays, so stopping the server leaves them running and the next server adopts them.
/// </summary>
public sealed class SessionHostService : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _historyLoads = new();
    private readonly ConcurrentDictionary<string, string> _historyErrors = new();
    private readonly FuseraftCli _cli;
    private readonly HitlBroker  _hitlBroker;
    private readonly UserStore   _users;
    private readonly ModelProfileRegistry _profiles;
    private readonly ILogger<SessionHostService> _logger;
    private volatile bool _stopping;

    internal static TimeSpan StartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
    internal static TimeSpan CancelGrace    { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fired when a new session event arrives. Args: (sessionId, event).</summary>
    public event Action<string, SessionEvent>? EventFired;

    /// <summary>Fired when the session list changes (new session, status change, etc.).</summary>
    public event Action? SessionListChanged;

    public SessionHostService(FuseraftCli cli, HitlBroker hitlBroker, UserStore users, ModelProfileRegistry profiles,
        ILogger<SessionHostService> logger)
    {
        _cli        = cli;
        _hitlBroker = hitlBroker;
        _users      = users;
        _profiles   = profiles;
        _logger     = logger;
    }

    /// <summary>Why <paramref name="user"/>'s session history couldn't be loaded, if it couldn't.</summary>
    public string? HistoryError(WebUser user) => _historyErrors.GetValueOrDefault(user.Id);

    /// <summary>Lists the sessions in <paramref name="user"/>'s fuseraft home, once per server lifetime.</summary>
    public Task EnsureHistoryAsync(WebUser user) =>
        _historyLoads.GetOrAdd(user.Id, _ => new Lazy<Task>(() => LoadHistoryAsync(user))).Value;

    private async Task LoadHistoryAsync(WebUser user)
    {
        try
        {
            var sessions = await _cli.ForHome(user.Home).ListAsync<WireSession>("sessions", "--all", "--json");
            foreach (var s in sessions.OrderByDescending(s => s.LastUpdatedAt).Take(100))
                _sessions.TryAdd(s.SessionId, FromHistory(s, user.Id));
            SessionListChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _historyErrors[user.Id] = ex.Message;
            _logger.LogWarning(ex, "Could not load the session history of {User}", user.UserName);
        }
    }

    private static ManagedSession FromHistory(WireSession s, string ownerId) => new()
    {
        SessionId        = s.SessionId,
        OwnerId          = ownerId,
        Task             = s.Task,
        ConfigPath       = s.ConfigPath ?? string.Empty,
        WorkingDirectory = s.WorkingDirectory,
        Status           = s.IsComplete ? SessionStatus.Succeeded : SessionStatus.Idle,
        StartedAt        = s.StartedAt,
        EndedAt          = s.IsComplete ? s.LastUpdatedAt : null,
        Succeeded        = s.IsComplete ? true : null,
    };

    /// <summary>The sessions <paramref name="user"/> may see: their own, or everyone's for an admin.</summary>
    public IEnumerable<ManagedSession> GetSessions(WebUser user) =>
        _sessions.Values.Where(s => user.CanAccess(s.OwnerId)).OrderByDescending(s => s.StartedAt);

    public ManagedSession? GetSession(WebUser user, string sessionId) =>
        _sessions.GetValueOrDefault(sessionId) is { } s && user.CanAccess(s.OwnerId) ? s : null;

    /// <summary>
    /// Returns the session with its stored messages loaded, fetching them from fuseraft if it's from
    /// history (or was started outside this server). Null if <paramref name="user"/> can't see it or fuseraft doesn't know it.
    /// </summary>
    public async Task<ManagedSession?> LoadSessionAsync(WebUser user, string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var any) && !user.CanAccess(any.OwnerId))
            return null;
        var existing = any;
        if (existing is not null && (existing.HistoryLoaded || existing.Process is not null))
            return existing;

        var owner = existing is null ? user : _users.Find(existing.OwnerId) ?? user;
        WireSessionDetail detail;
        try
        {
            detail = await _cli.ForHome(owner.Home).ItemAsync<WireSessionDetail>("sessions", "--show", sessionId, "--json");
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
        }, owner.Id));
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

    public Task<string> StartSessionAsync(WebUser user, string task, string configPath, string workspaceDir = "")
    {
        var access = WorkspaceAccess.For(user);
        var cwd    = string.IsNullOrWhiteSpace(workspaceDir) ? null : access.Check(workspaceDir);
        if (cwd is null && access.Roots.Count > 0)
            throw new ArgumentException("Pick a workspace to run the session in.");
        var absConfig = access.Check(configPath, cwd);

        return LaunchAsync(user, ["run", "--bridge", "-c", absConfig, task], cwd, existing: null);
    }

    public async Task<string> ResumeSessionAsync(WebUser user, string sessionId)
    {
        var existing = await LoadSessionAsync(user, sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");
        if (existing.IsLive)
            throw new InvalidOperationException($"Session '{sessionId}' is already running.");
        if (existing.Status == SessionStatus.Succeeded)
            throw new InvalidOperationException($"Session '{sessionId}' is already complete.");

        // A session resumes as the account that started it, with that account's home and keys.
        var owner = _users.Find(existing.OwnerId)
            ?? throw new InvalidOperationException("The account that started this session no longer exists.");
        var cwd = existing.WorkingDirectory is { Length: > 0 } wd && Directory.Exists(wd) ? wd : null;
        return await LaunchAsync(owner, ["run", "--bridge", "--resume", sessionId], cwd, existing);
    }

    /// <summary>
    /// Starts a bridge process and waits for its <c>ready</c> event, which carries the session ID.
    /// A run that fails before starting sends only <c>result</c>; that becomes the exception.
    /// </summary>
    private async Task<string> LaunchAsync(WebUser owner, string[] args, string? cwd, ManagedSession? existing)
    {
        var keys = await _profiles.For(owner).KeyEnvironmentAsync();
        var tag  = new BridgeTag { Kind = Kind, OwnerId = owner.Id, StartedAt = existing?.StartedAt ?? DateTimeOffset.UtcNow };
        if (cwd is not null) tag.Extra["cwd"] = cwd;
        var proc = await _cli.ForHome(owner.Home).StartBridgeAsync(args, cwd, keys, tag);
        return await AdoptAsync(owner.Id, proc, cwd, existing, tag.StartedAt);
    }

    /// <summary>The <see cref="BridgeTag.Kind"/> of a session's relay.</summary>
    public const string Kind = "session";

    /// <summary>
    /// Takes over a session a previous server left running. Its relay replays everything so far, so the page
    /// rebuilds as it was, and approvals still waiting are asked again.
    /// </summary>
    public async Task AdoptRunningAsync(BridgeTag tag, IBridgeProcess proc)
    {
        if (_users.Find(tag.OwnerId) is null)
        {
            // Nobody can see or answer it any more.
            proc.Kill();
            proc.Dispose();
            return;
        }
        try
        {
            await AdoptAsync(tag.OwnerId, proc, tag.Extra.GetValueOrDefault("cwd"), existing: null, tag.StartedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't take over a running session");
        }
    }

    private async Task<string> AdoptAsync(string ownerId, IBridgeProcess proc, string? cwd, ManagedSession? existing,
        DateTimeOffset startedAt)
    {
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
            OwnerId          = ownerId,
            Task             = Str(ready, "task") ?? existing?.Task ?? string.Empty,
            ConfigPath       = Str(ready, "config") ?? existing?.ConfigPath ?? string.Empty,
            WorkingDirectory = cwd ?? existing?.WorkingDirectory,
            Status           = SessionStatus.Running,
            StartedAt        = startedAt,
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
        var relay  = session.Relay ??= NewRelay(session, proc);
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
        if (_stopping) return; // the server let go; the session runs on in its relay
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

        // The reason goes in before the status, so a page that sees the session end also sees why.
        if (succeeded)
            session.Status = SessionStatus.Succeeded;
        else if (session.CancelRequested)
        {
            session.ErrorMessage = "Cancelled by user.";
            session.Status       = SessionStatus.Cancelled;
        }
        else
        {
            session.ErrorMessage = result is { } failed
                ? Str(failed, "errorMessage") ?? $"The session failed (exit {exit})."
                : $"fuseraft exited unexpectedly (exit {exit}). {stderrTail.Split('\n').LastOrDefault()}".Trim();
            session.Status       = SessionStatus.Failed;
        }

        Emit(session, new SessionEvent
        {
            Type = "session_end", Succeeded = succeeded, ErrorMessage = succeeded ? null : session.ErrorMessage,
        });
        SessionListChanged?.Invoke();
    }

    private ApprovalRelay NewRelay(ManagedSession session, IBridgeProcess proc) =>
        new(_hitlBroker, proc.Input, ApprovalSource.Session, ownerId: session.OwnerId);

    /// <summary>Asks the session to stop; kills its process if it hasn't exited after <see cref="CancelGrace"/>.</summary>
    public void CancelSession(WebUser user, string sessionId)
    {
        if (GetSession(user, sessionId) is not { Process: { } proc } s) return;
        s.CancelRequested = true;
        _hitlBroker.CancelSession(sessionId);
        _ = (s.Relay ??= NewRelay(s, proc)).InterruptAsync();
        _ = KillIfStillRunningAsync(proc);
    }

    private static async Task KillIfStillRunningAsync(IBridgeProcess proc)
    {
        try { await proc.WaitForExitAsync().WaitAsync(CancelGrace); }
        catch (TimeoutException) { proc.Kill(); }
        catch { /* disposed: it exited */ }
    }

    public async Task DeleteSessionAsync(WebUser user, string sessionId)
    {
        var s = GetSession(user, sessionId) ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");
        if (s.IsLive)
            throw new InvalidOperationException("Cancel the session before deleting it.");

        var owner = _users.Find(s.OwnerId) ?? user;
        await _cli.ForHome(owner.Home).RunAsync("sessions", "--delete", sessionId);
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

    /// <summary>Lets go of every running session; their relays keep them running for the next server.</summary>
    public void Dispose()
    {
        _stopping = true;
        foreach (var s in _sessions.Values)
            s.Process?.Dispose();
    }
}
