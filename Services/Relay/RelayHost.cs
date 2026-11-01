using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace fuseraft.Web.Services.Relay;

/// <summary>
/// <c>fuseraft-web relay &lt;dir&gt;</c>: runs one fuseraft process for the server and outlives it. It holds the
/// process's stdio, logs every line both ways, and serves the log then the live lines to whichever server
/// connects to its socket, so a restarted server picks the session up where it was.
/// </summary>
public sealed class RelayHost : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<string> _log = [];
    private readonly StreamWriter _logFile;
    private readonly IRelayChild _child;
    private readonly Socket _listener;
    private readonly string _socketPath;
    private Socket? _client;
    private NetworkStream? _clientStream;

    private RelayHost(IRelayChild child, string logPath, string socketPath)
    {
        _child      = child;
        _socketPath = socketPath;
        _logFile    = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        if (File.Exists(socketPath)) File.Delete(socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(4);
    }

    /// <summary>The relay process's entry point.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [var dir]) return 2;

        // Out of the server's session and process group, so a Ctrl+C or a hangup aimed at the server misses it.
        if (!OperatingSystem.IsWindows()) _ = setsid();
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        var meta  = RelayMeta.Read(dir);
        var child = ProcessChild.Start(meta);
        await using var host = Start(child, Path.Combine(dir, "log"), meta.SocketPath);
        using var term = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => child.Kill());
        return await host.RunToExitAsync();
    }

    /// <summary>Starts relaying <paramref name="child"/>; tests run it in-process.</summary>
    internal static RelayHost Start(IRelayChild child, string logPath, string socketPath)
    {
        var host = new RelayHost(child, logPath, socketPath);
        _ = host.AcceptLoopAsync();
        return host;
    }

    /// <summary>Relays until the child exits, then records its exit code and returns it.</summary>
    internal async Task<int> RunToExitAsync()
    {
        var stdout = PumpAsync(_child.Output, RelayLine.Output);
        var stderr = PumpAsync(_child.Error, RelayLine.Error);
        var exit   = await _child.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        Record($"{RelayLine.Exit} {exit}");
        // Closed now, so a server removing the relay's files isn't blocked by it (Windows won't delete an open file).
        lock (_lock) _logFile.Dispose();
        return exit;
    }

    private async Task PumpAsync(TextReader reader, char kind)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                Record($"{kind} {line}");
        }
        catch { /* the child's pipe closed */ }
    }

    private void Record(string entry)
    {
        lock (_lock)
        {
            _log.Add(entry);
            try { _logFile.WriteLine(entry); } catch { /* the log is for the record; the live line still goes out */ }
            Send(entry);
        }
    }

    // Under _lock. A server that's gone is dropped; the next one gets the whole log anyway.
    private void Send(string entry)
    {
        if (_clientStream is null) return;
        try
        {
            _clientStream.Write(Encoding.UTF8.GetBytes(entry + "\n"));
        }
        catch
        {
            DropClient();
        }
    }

    private void DropClient()
    {
        try { _client?.Dispose(); } catch { }
        _client = null;
        _clientStream = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            Socket socket;
            try { socket = await _listener.AcceptAsync(); }
            catch { return; } // disposed
            // A server that stops reading must not stall the session: a send that can't finish drops it instead.
            socket.SendTimeout = 10_000;
            var stream = new NetworkStream(socket, ownsSocket: false);
            lock (_lock)
            {
                // A new connection is a restarted server: it replaces the old one and gets everything so far.
                DropClient();
                _client       = socket;
                _clientStream = stream;
                foreach (var entry in _log) Send(entry);
                Send(RelayLine.ReplayDone.ToString());
            }
            _ = ReadClientAsync(socket, stream);
        }
    }

    private async Task ReadClientAsync(Socket socket, NetworkStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line == RelayLine.Kill.ToString())
                {
                    _child.Kill();
                }
                else if (line.StartsWith($"{RelayLine.Input} "))
                {
                    var input = line[2..];
                    lock (_lock)
                    {
                        try { _child.Input.WriteLine(input); } catch { /* the child is gone */ }
                        _log.Add(line);
                        try { _logFile.WriteLine(line); } catch { }
                    }
                }
            }
        }
        catch { /* the server went away */ }
        lock (_lock)
        {
            if (_client == socket) DropClient();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock) DropClient();
        _listener.Dispose();
        try { File.Delete(_socketPath); } catch { }
        await _logFile.DisposeAsync();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
}

/// <summary>The relay's line format, on the socket and in its log: a kind letter, a space, then the line.</summary>
public static class RelayLine
{
    public const char Output     = 'o';
    public const char Error      = 'e';
    public const char Input      = 'i';
    public const char Exit       = 'x';
    public const char ReplayDone = 'r';
    public const char Kill       = 'k';
}

/// <summary>What a relay runs and how it's found again, in <c>meta.json</c> next to its log. Never holds secrets.</summary>
public sealed class RelayMeta
{
    public string        Command    { get; set; } = "";
    public List<string>  Args       { get; set; } = [];
    public string?       Cwd        { get; set; }
    public string        SocketPath { get; set; } = "";
    public BridgeTag     Tag        { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static RelayMeta Read(string dir) =>
        JsonSerializer.Deserialize<RelayMeta>(File.ReadAllText(Path.Combine(dir, "meta.json")), JsonOpts)
        ?? throw new InvalidOperationException($"No relay metadata in {dir}.");

    public void Write(string dir) =>
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(this, JsonOpts));
}

/// <summary>Who a relayed process belongs to and what it is, so a restarted server can adopt it.</summary>
public sealed class BridgeTag
{
    /// <summary><c>session</c> or <c>repl</c>.</summary>
    public string Kind    { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, string> Extra { get; set; } = [];
}

/// <summary>The process a relay runs: a real fuseraft process, or a scripted one in tests.</summary>
internal interface IRelayChild
{
    TextReader Output { get; }
    TextReader Error  { get; }
    TextWriter Input  { get; }
    Task<int> WaitForExitAsync();
    void Kill();
}

internal sealed class ProcessChild : IRelayChild
{
    private readonly Process _proc;

    private ProcessChild(Process proc)
    {
        _proc = proc;
        _proc.StandardInput.AutoFlush = true;
    }

    public static ProcessChild Start(RelayMeta meta)
    {
        var psi = new ProcessStartInfo(meta.Command)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        if (!string.IsNullOrWhiteSpace(meta.Cwd)) psi.WorkingDirectory = meta.Cwd;
        foreach (var a in meta.Args) psi.ArgumentList.Add(a);
        return new ProcessChild(Process.Start(psi)!);
    }

    public TextReader Output => _proc.StandardOutput;
    public TextReader Error  => _proc.StandardError;
    public TextWriter Input  => _proc.StandardInput;

    public async Task<int> WaitForExitAsync()
    {
        await _proc.WaitForExitAsync();
        return _proc.ExitCode;
    }

    public void Kill()
    {
        try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
