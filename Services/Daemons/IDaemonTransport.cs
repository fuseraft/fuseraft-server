using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using fuseraft.Web.Models;

namespace fuseraft.Web.Services.Daemons;

/// <summary>A line-oriented connection to a daemon's attach endpoint.</summary>
public interface IDaemonTransport : IAsyncDisposable
{
    /// <param name="clientId">Sent so the daemon keeps this server's tasks across reconnects.</param>
    Task ConnectAsync(DaemonEndpoint endpoint, string clientId, CancellationToken cancellationToken);

    Task SendLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>The next line, or null once the daemon closes the connection.</summary>
    Task<string?> ReceiveLineAsync(CancellationToken cancellationToken);
}

/// <summary>The attach protocol over a WebSocket: each text message is one line.</summary>
public sealed class WebSocketDaemonTransport : IDaemonTransport
{
    private const int MaxMessageBytes = 4 << 20;
    public const string ClientIdHeader = "Fuseraft-Client-Id";

    private readonly ClientWebSocket _socket = new();
    // Approval answers and dispatches come from different flows; a WebSocket allows one send at a time.
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Queue<string> _pending = new();

    public async Task ConnectAsync(DaemonEndpoint endpoint, string clientId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(endpoint.Token))
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
        _socket.Options.SetRequestHeader(ClientIdHeader, clientId);
        // Pings notice a daemon that vanished without closing, so the reconnect starts within its grace period.
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _socket.Options.KeepAliveTimeout  = TimeSpan.FromSeconds(30);
        if (NormalizeFingerprint(endpoint.CertificateSha256) is { } pinned)
            _socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert is not null && Fingerprint(cert) == pinned;
        await _socket.ConnectAsync(new Uri(endpoint.Url), cancellationToken);
    }

    /// <summary>A fingerprint as 64 upper-case hex digits, from any of the usual spellings; null when it isn't one.</summary>
    public static string? NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var hex = new string([.. fingerprint.Where(c => c is not (':' or ' ' or '-'))]).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    private static string Fingerprint(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

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
