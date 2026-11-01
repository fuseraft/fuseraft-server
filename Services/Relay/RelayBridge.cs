using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using fuseraft.Web.Services.Cli;

namespace fuseraft.Web.Services.Relay;

/// <summary>
/// A fuseraft process run through a relay (<see cref="RelayHost"/>), seen as an ordinary <see cref="IBridgeProcess"/>.
/// Connecting replays everything the process has said so far, minus approval requests that were already answered,
/// with what was typed into it surfaced as <c>replayed_input</c> events, so a restarted server rebuilds the same
/// picture and only re-asks the approvals still open.
/// </summary>
public sealed class RelayBridge : IBridgeProcess
{
    /// <summary>A replayed line someone typed: <c>{"type":"replayed_input","text":…,"raw":…}</c>.</summary>
    public const string ReplayedInput = "replayed_input";

    private const int StderrLines = 20;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly string _dir;
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _stderr = new();
    private readonly Lock _sendLock = new();
    private volatile bool _exited;
    private volatile bool _detached;

    private RelayBridge(Socket socket, string dir)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _dir    = dir;
        Output  = new ChannelLineReader(_output.Reader);
        Input   = new RelayInputWriter(this);
        _ = ReadAsync();
    }

    public string Directory => _dir;
    public TextReader Output { get; }
    public TextWriter Input  { get; }

    public string StderrTail
    {
        get { lock (_stderr) return string.Join('\n', _stderr); }
    }

    /// <summary>Connects to the relay in <paramref name="dir"/>; null if it isn't running.</summary>
    public static async Task<RelayBridge?> ConnectAsync(string dir, string socketPath, TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + (wait ?? TimeSpan.Zero);
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                return new RelayBridge(socket, dir);
            }
            catch (SocketException)
            {
                socket.Dispose();
                if (DateTime.UtcNow >= deadline) return null;
                await Task.Delay(50);
            }
        }
    }

    public Task<int> WaitForExitAsync() => _exit.Task;

    public void Kill() => Send(RelayLine.Kill.ToString());

    /// <summary>
    /// Lets go of the relay, which keeps the process running. Once the process has exited, its relay's files go too.
    /// After letting go, <see cref="WaitForExitAsync"/> never completes: the process didn't exit, the server left.
    /// </summary>
    public void Dispose()
    {
        _detached = !_exited;
        try { _stream.Dispose(); } catch { }
        if (_exited)
            try { System.IO.Directory.Delete(_dir, recursive: true); } catch { }
    }

    internal void Send(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_sendLock)
        {
            try { _stream.Write(bytes); } catch { /* the relay is gone; WaitForExitAsync says so */ }
        }
    }

    private async Task ReadAsync()
    {
        using var reader = new StreamReader(_stream, Encoding.UTF8);
        var backlog = new List<string>();
        var replaying = true;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                if (replaying)
                {
                    if (line[0] == RelayLine.ReplayDone)
                    {
                        replaying = false;
                        foreach (var entry in Replay(backlog)) Handle(entry, live: false);
                        backlog.Clear();
                    }
                    else backlog.Add(line);
                    continue;
                }
                Handle(line, live: true);
            }
        }
        catch { /* the connection dropped */ }
        _output.Writer.TryComplete();
        // No exit line means the relay itself died, and the process with it, unless this side let go.
        if (!_detached) _exit.TrySetResult(-1);
    }

    private void Handle(string entry, bool live)
    {
        if (entry.Length < 1) return;
        var body = entry.Length > 2 ? entry[2..] : "";
        switch (entry[0])
        {
            case RelayLine.Output:
                _output.Writer.TryWrite(body);
                break;
            case RelayLine.Error:
                lock (_stderr)
                {
                    _stderr.Enqueue(body);
                    if (_stderr.Count > StderrLines) _stderr.Dequeue();
                }
                break;
            case RelayLine.Input when !live:
                // Only during replay, as Replay already rewrote it; live input is our own echo.
                _output.Writer.TryWrite(body);
                break;
            case RelayLine.Exit:
                _exited = true;
                _output.Writer.TryComplete();
                _exit.TrySetResult(int.TryParse(body, out var code) ? code : -1);
                break;
        }
    }

    /// <summary>
    /// The backlog as a new server should see it. Answers go to the oldest open request, so the first N requests
    /// were answered by the first N answers; those are dropped. What was typed becomes a <c>replayed_input</c> line.
    /// </summary>
    internal static IEnumerable<string> Replay(IReadOnlyList<string> backlog)
    {
        var answered = new HashSet<int>();
        var open = new Queue<int>();
        for (var i = 0; i < backlog.Count; i++)
        {
            var entry = backlog[i];
            if (entry.Length < 2) continue;
            var type = TypeOf(entry[2..]);
            if (entry[0] == RelayLine.Output && type == "approval_request") open.Enqueue(i);
            else if (entry[0] == RelayLine.Input && type == "approval_response" && open.TryDequeue(out var req)) answered.Add(req);
        }

        for (var i = 0; i < backlog.Count; i++)
        {
            var entry = backlog[i];
            if (answered.Contains(i) || entry.Length < 1) continue;
            if (entry[0] != RelayLine.Input)
            {
                yield return entry;
                continue;
            }

            var body = entry.Length > 2 ? entry[2..] : "";
            var type = TypeOf(body);
            if (type is "approval_response" or "interrupt") continue;
            var text = type == "user_input" ? TextOf(body) : type is null ? body : null;
            if (text is null) continue;
            yield return $"{RelayLine.Input} " + JsonSerializer.Serialize(new { type = ReplayedInput, text, raw = type is null });
        }
    }

    private static string? TypeOf(string line)
    {
        if (line.Length == 0 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? TextOf(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    }

    /// <summary>Line-buffered, so every <c>WriteLine</c> reaches the relay as one <c>i</c> line.</summary>
    private sealed class RelayInputWriter(RelayBridge bridge) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    var line = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                    bridge.Send($"{RelayLine.Input} {line}");
                }
                else _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var c in value) Write(c);
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A <see cref="TextReader"/> over a channel of lines.</summary>
internal sealed class ChannelLineReader(ChannelReader<string> lines) : TextReader
{
    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        await lines.WaitToReadAsync(cancellationToken) && lines.TryRead(out var line) ? line : null;

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
}
