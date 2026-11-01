using fuseraft.Web.Services.Users;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services.Daemons;

/// <summary>
/// The <c>fuseraft serve</c> daemons this server can attach to: those in <c>Fuseraft:Daemons</c>, plus those added on
/// the Daemons page, which <see cref="DaemonStore"/> keeps. Each one with <see cref="DaemonEndpoint.AutoConnect"/>
/// connects when the server starts.
/// </summary>
public sealed class DaemonService : IHostedService
{
    private readonly HitlBroker _broker;
    private readonly Func<IDaemonTransport> _transportFactory;
    private readonly DaemonStore? _store;
    private readonly ILogger<DaemonService>? _logger;
    private readonly Lock _lock = new();
    private readonly List<DaemonConnection> _connections = [];

    public DaemonService(IConfiguration configuration, HitlBroker broker, DaemonStore store, ILogger<DaemonService> logger)
        : this(configuration.GetSection("Fuseraft:Daemons").Get<List<DaemonEndpoint>>() ?? [], broker,
               () => new WebSocketDaemonTransport(), store, logger)
    {
    }

    /// <param name="store">Null keeps page-added daemons in memory only, with a fresh client id each time.</param>
    public DaemonService(IEnumerable<DaemonEndpoint> configured, HitlBroker broker, Func<IDaemonTransport> transportFactory,
        DaemonStore? store = null, ILogger<DaemonService>? logger = null)
    {
        _broker           = broker;
        _transportFactory = transportFactory;
        _store            = store;
        _logger           = logger;
        foreach (var endpoint in configured)
        {
            if (Validate(endpoint.Name, endpoint.Url, endpoint.CertificateSha256) is { } problem)
            {
                _logger?.LogWarning("Skipping daemon '{Name}' in Fuseraft:Daemons: {Problem}", endpoint.Name, problem);
                continue;
            }
            _connections.Add(NewConnection(endpoint, configured: true));
        }
        foreach (var endpoint in store?.Added() ?? [])
            if (Validate(endpoint.Name, endpoint.Url, endpoint.CertificateSha256) is null)
                _connections.Add(NewConnection(endpoint, configured: false));
    }

    public event Action? Changed;

    public IReadOnlyList<DaemonConnection> Connections
    {
        get { lock (_lock) return [.. _connections]; }
    }

    public DaemonConnection? Get(Guid id)
    {
        lock (_lock) return _connections.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The daemons <paramref name="user"/> may use: every one for an admin, otherwise those that list them.</summary>
    public IReadOnlyList<DaemonConnection> For(WebUser user) => [.. Connections.Where(c => c.Endpoint.Allows(user))];

    /// <summary>Sets which non-admins may use a daemon added on the page; configured ones take theirs from <c>Fuseraft:Daemons</c>.</summary>
    public void SetUsers(DaemonConnection connection, IEnumerable<string> userNames)
    {
        if (connection.Configured) throw new InvalidOperationException("This daemon is configured in Fuseraft:Daemons; set its Users there.");
        connection.Endpoint.Users = [.. userNames.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        SaveAdded();
        Changed?.Invoke();
    }

    /// <summary>Null when a daemon can be added under this name and URL, otherwise why not.</summary>
    public string? Validate(string? name, string? url, string? certificateSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            return "URL must be a ws:// or wss:// address, such as the \"Attach WebSocket\" line fuseraft serve prints.";
        if (!string.IsNullOrWhiteSpace(certificateSha256))
        {
            if (WebSocketDaemonTransport.NormalizeFingerprint(certificateSha256) is null)
                return "The certificate fingerprint must be a SHA-256 fingerprint: 64 hex digits, colons allowed.";
            if (uri.Scheme != "wss") return "A certificate fingerprint needs a wss:// URL.";
        }
        return Find(name.Trim()) is null ? null : $"A daemon named '{name.Trim()}' already exists.";
    }

    public DaemonConnection Add(string name, string url, string? token, string? certificateSha256 = null)
    {
        if (Validate(name, url, certificateSha256) is { } problem) throw new ArgumentException(problem);
        var endpoint = new DaemonEndpoint
        {
            Name              = name.Trim(),
            Url               = url.Trim(),
            Token             = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            CertificateSha256 = string.IsNullOrWhiteSpace(certificateSha256) ? null : certificateSha256.Trim(),
        };
        var connection = NewConnection(endpoint, configured: false);
        lock (_lock) _connections.Add(connection);
        SaveAdded();
        Changed?.Invoke();
        return connection;
    }

    /// <summary>Connects, and for a page-added daemon remembers to connect it again after a restart.</summary>
    public Task ConnectAsync(DaemonConnection connection)
    {
        Remember(connection, autoConnect: true);
        return connection.ConnectAsync();
    }

    /// <summary>Disconnects, and for a page-added daemon remembers to leave it disconnected after a restart.</summary>
    public Task DisconnectAsync(DaemonConnection connection)
    {
        Remember(connection, autoConnect: false);
        return connection.DisconnectAsync();
    }

    /// <summary>Disconnects and forgets a daemon added on the page; configured ones stay.</summary>
    public async Task RemoveAsync(Guid id)
    {
        var connection = Get(id);
        if (connection is null || connection.Configured) return;
        await connection.DisconnectAsync();
        lock (_lock) _connections.Remove(connection);
        SaveAdded();
        _store?.Forget(connection.Endpoint.Name);
        Changed?.Invoke();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var c in Connections)
        {
            if (c.Endpoint.Unencrypted)
                _logger?.LogWarning("Daemon '{Name}' is attached over plain ws:// to another machine; its token and tasks travel " +
                                    "unencrypted. Give the daemon --tls-cert and use wss://.", c.Endpoint.Name);
            if (c.Endpoint.AutoConnect) _ = ConnectAndCheckAsync(c);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes every connection without changing what's remembered. The daemons keep running their tasks, and wait
    /// their reconnect grace for this server to come back with an approval answer.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Connections.Select(c => c.DisconnectAsync())).WaitAsync(cancellationToken);

    private DaemonConnection NewConnection(DaemonEndpoint endpoint, bool configured) =>
        new(endpoint, _broker, _transportFactory, configured, _store?.ClientId(endpoint.Name), _store);

    private async Task ConnectAndCheckAsync(DaemonConnection connection)
    {
        await connection.ConnectAsync();
        if (connection.State == DaemonState.Connected && !connection.SupportsReconnect)
            _logger?.LogWarning("Daemon '{Name}' runs a fuseraft older than v1.14.0: if its connection drops, a pending approval is " +
                                "decided by its --unattended-policy and results that finish meanwhile are lost. Upgrade fuseraft there.",
                connection.Endpoint.Name);
    }

    private void Remember(DaemonConnection connection, bool autoConnect)
    {
        if (connection.Configured || connection.Endpoint.AutoConnect == autoConnect) return;
        connection.Endpoint.AutoConnect = autoConnect;
        SaveAdded();
    }

    private void SaveAdded() =>
        _store?.SaveAdded(Connections.Where(c => !c.Configured).Select(c => c.Endpoint));

    private DaemonConnection? Find(string name)
    {
        lock (_lock) return _connections.FirstOrDefault(c => c.Endpoint.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
