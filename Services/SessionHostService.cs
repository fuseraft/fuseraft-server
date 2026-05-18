using System.Collections.Concurrent;
using System.Diagnostics;
using fuseraft.Cli;
using fuseraft.Core;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Server.Models;

namespace fuseraft.Server.Services;

/// <summary>
/// Singleton that manages the lifecycle of all orchestration sessions within the server process.
/// Provides start/cancel operations, fires C# events consumed by Blazor components,
/// and pre-loads historical sessions from the global session store on startup.
/// </summary>
public sealed class SessionHostService : IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new();
    private readonly PluginRegistry   _pluginRegistry;
    private readonly HitlBroker       _hitlBroker;
    private readonly ILoggerFactory   _loggerFactory;
    private readonly JsonSessionStore _sessionStore;

    /// <summary>Fired when a new session event arrives. Args: (sessionId, event).</summary>
    public event Action<string, SessionEvent>? EventFired;

    /// <summary>Fired when the session list changes (new session, status change, etc.).</summary>
    public event Action? SessionListChanged;

    public SessionHostService(
        PluginRegistry pluginRegistry,
        HitlBroker     hitlBroker,
        ILoggerFactory loggerFactory)
    {
        _pluginRegistry = pluginRegistry;
        _hitlBroker     = hitlBroker;
        _loggerFactory  = loggerFactory;
        _sessionStore   = new JsonSessionStore(
            loggerFactory.CreateLogger<JsonSessionStore>(),
            FuseraftPaths.GlobalSessions);

        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var checkpoints = await _sessionStore.ListAsync();
            foreach (var cp in checkpoints
                .OrderByDescending(c => c.LastUpdatedAt)
                .Take(100))
            {
                var session = new ManagedSession
                {
                    SessionId  = cp.SessionId,
                    Task       = cp.Task ?? string.Empty,
                    ConfigPath = cp.ConfigPath ?? string.Empty,
                    Status     = cp.IsComplete ? SessionStatus.Succeeded : SessionStatus.Idle,
                    StartedAt  = cp.LastUpdatedAt,
                    EndedAt    = cp.IsComplete ? cp.LastUpdatedAt : null,
                    Succeeded  = cp.IsComplete ? true : null,
                };
                session.AddMessages(cp.Messages);
                // Synthesize message events so SessionDetail can render history
                foreach (var msg in cp.Messages)
                    session.AddEvent(new SessionEvent { Type = "message", Message = msg });
                if (cp.IsComplete)
                    session.AddEvent(new SessionEvent { Type = "session_end", Succeeded = true });

                _sessions.TryAdd(cp.SessionId, session);
            }
        }
        catch { /* history load is best-effort */ }
    }

    public IEnumerable<ManagedSession> GetSessions() =>
        _sessions.Values.OrderByDescending(s => s.StartedAt);

    public ManagedSession? GetSession(string sessionId) =>
        _sessions.GetValueOrDefault(sessionId);

    public async Task<string> StartSessionAsync(
        string task,
        string configPath,
        string workspaceDir      = "",
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var cts = new CancellationTokenSource();
        var session = new ManagedSession
        {
            SessionId    = sessionId,
            Task         = task,
            ConfigPath   = configPath,
            WorkspaceDir = workspaceDir,
            Status       = SessionStatus.Starting,
            StartedAt    = DateTimeOffset.UtcNow,
            Cts          = cts,
        };

        _sessions[sessionId] = session;
        SessionListChanged?.Invoke();

        _ = Task.Run(() => RunSessionAsync(session, cts.Token), CancellationToken.None);
        return sessionId;
    }

    public void CancelSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.Cts?.Cancel();
            _hitlBroker.CancelSession(sessionId);
        }
    }

    private void Emit(ManagedSession session, SessionEvent evt)
    {
        session.AddEvent(evt);
        EventFired?.Invoke(session.SessionId, evt);
    }

    private async Task RunSessionAsync(ManagedSession session, CancellationToken ct)
    {
        try
        {
            var absConfig = Path.IsPathRooted(session.ConfigPath)
                ? session.ConfigPath
                : !string.IsNullOrEmpty(session.WorkspaceDir)
                    ? Path.GetFullPath(Path.Combine(session.WorkspaceDir, session.ConfigPath))
                    : Path.GetFullPath(session.ConfigPath);
            var hitlService = new WebHumanApprovalService(_hitlBroker, session.SessionId);

            var (orchestrator, config, mcpManager, compactor, changeTracker, eventEmitter, govKernel, skillCurator) =
                await OrchestratorBuilder.BuildAsync(
                    absConfig, _loggerFactory, _pluginRegistry, hitlService,
                    hitlMode: false, cancellationToken: ct);

            await using var _mcp = mcpManager;
            using  var _gov = govKernel;

            await OrchestratorBuilder.ValidateApiKeysAsync(config);

            var checkpoint = new SessionCheckpoint
            {
                SessionId  = session.SessionId,
                Task       = session.Task,
                ConfigPath = absConfig,
            };

            eventEmitter?.SetSessionId(session.SessionId);
            orchestrator.SetSessionId(session.SessionId);
            orchestrator.SetStructuredTask(TaskModel.FromGoal(session.Task));

            session.Status = SessionStatus.Running;
            Emit(session, new SessionEvent { Type = "session_start" });
            SessionListChanged?.Invoke();

            orchestrator.AgentStarting += name =>
                Emit(session, new SessionEvent { Type = "agent_starting", AgentName = name });

            var turnClock = Stopwatch.StartNew();

            await foreach (var msg in orchestrator.StreamAsync(session.Task, checkpoint.Messages, ct))
            {
                var elapsed = turnClock.Elapsed;
                turnClock.Restart();

                session.AddMessage(msg);
                checkpoint.Messages.Add(msg);
                checkpoint.LastUpdatedAt = DateTime.UtcNow;

                Emit(session, new SessionEvent { Type = "message", Message = msg, Elapsed = elapsed });
                await _sessionStore.SaveAsync(checkpoint, CancellationToken.None);
            }

            checkpoint.IsComplete    = true;
            checkpoint.LastUpdatedAt = DateTime.UtcNow;
            session.Status           = SessionStatus.Succeeded;
            session.Succeeded        = true;
            session.EndedAt          = DateTimeOffset.UtcNow;

            await _sessionStore.SaveAsync(checkpoint, CancellationToken.None);
            Emit(session, new SessionEvent { Type = "session_end", Succeeded = true });
        }
        catch (OperationCanceledException)
        {
            session.Status    = SessionStatus.Cancelled;
            session.Succeeded = false;
            session.EndedAt   = DateTimeOffset.UtcNow;
            Emit(session, new SessionEvent
            {
                Type = "session_end", Succeeded = false, ErrorMessage = "Cancelled by user."
            });
        }
        catch (Exception ex)
        {
            session.Status       = SessionStatus.Failed;
            session.Succeeded    = false;
            session.ErrorMessage = ex.Message;
            session.EndedAt      = DateTimeOffset.UtcNow;
            Emit(session, new SessionEvent
            {
                Type = "session_end", Succeeded = false, ErrorMessage = ex.Message
            });
        }
        finally
        {
            SessionListChanged?.Invoke();
        }
    }

    public void Dispose() { /* CTS are owned by ManagedSession and cancelled via CancelSession */ }
}
