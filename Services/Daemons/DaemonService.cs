using fuseraft.Server.Models;

namespace fuseraft.Server.Services.Daemons;

/// <summary>
/// The <c>fuseraft serve</c> daemons this server can attach to: those in <c>Fuseraft:Daemons</c>, plus any
/// added on the Daemons page, which last until the server stops.
/// </summary>
public sealed class DaemonService
{
    private readonly HitlBroker _broker;
    private readonly Func<IDaemonTransport> _transportFactory;
    private readonly Lock _lock = new();
    private readonly List<DaemonConnection> _connections = [];

    public DaemonService(IConfiguration configuration, HitlBroker broker)
        : this(configuration.GetSection("Fuseraft:Daemons").Get<List<DaemonEndpoint>>() ?? [], broker,
               () => new WebSocketDaemonTransport())
    {
    }

    public DaemonService(IEnumerable<DaemonEndpoint> configured, HitlBroker broker, Func<IDaemonTransport> transportFactory)
    {
        _broker           = broker;
        _transportFactory = transportFactory;
        foreach (var endpoint in configured)
            if (Validate(endpoint.Name, endpoint.Url) is null && Find(endpoint.Name) is null)
                _connections.Add(new DaemonConnection(endpoint, broker, transportFactory, configured: true));
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

    /// <summary>Null when a daemon can be added under this name and URL, otherwise why not.</summary>
    public string? Validate(string? name, string? url)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss"))
            return "URL must be a ws:// or wss:// address, such as the \"Attach WebSocket\" line fuseraft serve prints.";
        return Find(name.Trim()) is null ? null : $"A daemon named '{name.Trim()}' already exists.";
    }

    public DaemonConnection Add(string name, string url, string? token)
    {
        if (Validate(name, url) is { } problem) throw new ArgumentException(problem);
        var endpoint = new DaemonEndpoint
        {
            Name  = name.Trim(),
            Url   = url.Trim(),
            Token = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
        };
        var connection = new DaemonConnection(endpoint, _broker, _transportFactory, configured: false);
        lock (_lock) _connections.Add(connection);
        Changed?.Invoke();
        return connection;
    }

    /// <summary>Disconnects and forgets a daemon added on the page; configured ones stay.</summary>
    public async Task RemoveAsync(Guid id)
    {
        var connection = Get(id);
        if (connection is null || connection.Configured) return;
        await connection.DisconnectAsync();
        lock (_lock) _connections.Remove(connection);
        Changed?.Invoke();
    }

    private DaemonConnection? Find(string name)
    {
        lock (_lock) return _connections.FirstOrDefault(c => c.Endpoint.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
