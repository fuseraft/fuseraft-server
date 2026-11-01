using System.Net.WebSockets;
using System.Text;

namespace fuseraft.Web.Services.Daemons;

/// <summary>A line-oriented connection to a daemon's attach endpoint.</summary>
public interface IDaemonTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, string? token, CancellationToken cancellationToken);

    Task SendLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The next line, or null once the daemon closes the connection.</summary>
    Task<string?> ReceiveLineAsync(CancellationToken cancellationToken);
}

/// <summary>The attach protocol over a WebSocket: each text message is one line.</summary>
public sealed class WebSocketDaemonTransport : IDaemonTransport
{
    private const int MaxMessageBytes = 4 << 20;

    private readonly ClientWebSocket _socket = new();
    // Approval answers and dispatches come from different flows; a WebSocket allows one send at a time.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Queue<string> _pending = new();

    public async Task ConnectAsync(Uri url, string? token, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(token))
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        await _socket.ConnectAsync(url, cancellationToken);
    }

    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try { await _socket.SendAsync(Encoding.UTF8.GetBytes(line), WebSocketMessageType.Text, endOfMessage: true, cancellationToken); }
        finally { _sendLock.Release(); }
    }

    public async Task<string?> ReceiveLineAsync(CancellationToken cancellationToken)
    {
        while (_pending.Count == 0)
        {
            var message = await ReceiveMessageAsync(cancellationToken);
            if (message is null) return null;
            foreach (var line in message.Split('\n'))
                if (line.TrimEnd('\r') is { Length: > 0 } l) _pending.Enqueue(l);
        }
        return _pending.Dequeue();
    }

    private async Task<string?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await _socket.ReceiveAsync(chunk, cancellationToken); }
            catch (WebSocketException) { return null; }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Write(chunk, 0, result.Count);
            if (buffer.Length > MaxMessageBytes) return null;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        _socket.Dispose();
        _sendLock.Dispose();
    }
}
