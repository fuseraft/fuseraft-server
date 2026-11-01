using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using fuseraft.Web.Services.Relay;
using fuseraft.Web.Services.Users;

namespace fuseraft.Web.Services.Cli;

public sealed class FuseraftCliOptions
{
    /// <summary>The fuseraft executable: a path, or a name resolved on PATH.</summary>
    public string Binary { get; set; } = "fuseraft";
}

public sealed class FuseraftCliException(string message) : Exception(message);

/// <summary>A running fuseraft process speaking the JSONL bridge (<c>run --bridge</c>, <c>repl --vscode</c>).</summary>
public interface IBridgeProcess : IDisposable
{
    TextReader Output { get; }
    TextWriter Input  { get; }
    /// <summary>The last lines the process wrote to stderr, for explaining an unexpected exit.</summary>
    string StderrTail { get; }
    Task<int> WaitForExitAsync();
    void Kill();
}

/// <summary>
/// The server's only way to reach fuseraft: it runs the CLI and speaks its wire protocol
/// (docs/protocol.md in fuseraft-cli) rather than linking fuseraft's assemblies. Sessions and chats run through
/// relays (<see cref="RelayHost"/>) so they outlive the server.
/// </summary>
public class FuseraftCli(IOptions<FuseraftCliOptions> options, IOptions<WebOptions>? web = null)
{
    private readonly RelayLauncher? _relays = web is null ? null : new RelayLauncher(web.Value.ResolvedDataDirectory);

    /// <summary>The protocol version this server was built against.</summary>
    public const int ProtocolVersion = 1;

    internal static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private string Binary => options.Value.Binary;

    /// <summary>The <c>FUSERAFT_HOME</c> every command runs against; null means fuseraft's own default.</summary>
    public string? Home { get; private init; }

    /// <summary>This CLI, running every command against <paramref name="home"/>: one user's sessions, memory and schedule.</summary>
    public virtual FuseraftCli ForHome(string home) => new(options, web) { Home = home };

    public async Task<List<T>> ListAsync<T>(params string[] args)
    {
        using var doc = await QueryAsync(args);
        return ParseList<T>(doc);
    }

    public async Task<T> ItemAsync<T>(params string[] args)
    {
        using var doc = await QueryAsync(args);
        return ParseItem<T>(doc);
    }

    /// <summary>Like <see cref="ItemAsync{T}"/>, for commands that act on their working directory.</summary>
    public async Task<T> ItemInAsync<T>(string workingDirectory, params string[] args)
    {
        using var doc = await QueryAsync(args, workingDirectory);
        return ParseItem<T>(doc);
    }

    /// <summary>Runs a command for its effect; a non-zero exit throws with what it printed.</summary>
    public virtual Task RunAsync(params string[] args) => RunAsync(null, args);

    /// <summary>Like <see cref="RunAsync(string[])"/>, with extra environment variables (such as a user's profile keys).</summary>
    public virtual async Task RunAsync(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var (exit, stdout, stderr) = await ExecAsync(args, environment: environment);
        if (exit != 0)
            throw new FuseraftCliException(FirstNonEmpty(stderr, stdout) ?? $"fuseraft {args.FirstOrDefault()} exited with code {exit}.");
    }

    /// <summary>
    /// Starts a bridge process. A tagged one runs in a relay, so it keeps running if the server stops and
    /// <see cref="ReattachBridgesAsync"/> finds it again.
    /// </summary>
    public virtual async Task<IBridgeProcess> StartBridgeAsync(IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null, BridgeTag? tag = null)
    {
        if (tag is null || _relays is null) return StartBridge(args, workingDirectory, environment);

        var env = new Dictionary<string, string>(environment ?? new Dictionary<string, string>());
        if (Home is not null)
        {
            Directory.CreateDirectory(Home);
            env["FUSERAFT_HOME"] = Home;
        }
        return await _relays.StartAsync(Binary, args, workingDirectory, env, tag);
    }

    /// <summary>The relayed processes a previous server left running, with what each one is.</summary>
    public virtual async Task<IReadOnlyList<(BridgeTag Tag, IBridgeProcess Process)>> ReattachBridgesAsync() =>
        _relays is null ? [] : [.. (await _relays.ReattachAsync()).Select(r => (r.Tag, (IBridgeProcess)r.Bridge))];

    public virtual IBridgeProcess StartBridge(IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = StartInfo(args, workingDirectory);
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        try
        {
            return new BridgeProcess(Process.Start(psi)!);
        }
        catch (Exception ex)
        {
            throw new FuseraftCliException($"Could not start '{Binary}': {ex.Message}");
        }
    }

    protected virtual async Task<JsonDocument> QueryAsync(string[] args, string? workingDirectory = null)
    {
        var (exit, stdout, stderr) = await ExecAsync(args, workingDirectory);
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (line is null)
            throw new FuseraftCliException(FirstNonEmpty(stderr) ?? $"fuseraft {string.Join(' ', args)} printed nothing (exit {exit}).");
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw new FuseraftCliException(
                $"fuseraft {string.Join(' ', args)} didn't print JSON. The installed fuseraft may predate protocol version {ProtocolVersion}.");
        }
    }

    internal static List<T> ParseList<T>(JsonDocument doc) =>
        Unwrap(doc, "items").Deserialize<List<T>>(JsonOpts) ?? [];

    internal static T ParseItem<T>(JsonDocument doc) =>
        Unwrap(doc, "item").Deserialize<T>(JsonOpts)
        ?? throw new FuseraftCliException("fuseraft returned an empty item.");

    private static JsonElement Unwrap(JsonDocument doc, string field)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FuseraftCliException("fuseraft printed JSON that isn't a protocol envelope.");
        CheckVersion(root);
        if (root.TryGetProperty("error", out var error))
            throw new FuseraftCliException(error.GetString() ?? "fuseraft reported an error.");
        if (!root.TryGetProperty(field, out var value))
            throw new FuseraftCliException($"fuseraft's response has no '{field}'.");
        return value;
    }

    /// <summary>Throws unless <paramref name="message"/> carries the protocol version this server speaks.</summary>
    public static void CheckVersion(JsonElement message)
    {
        if (!message.TryGetProperty("protocolVersion", out var v) || v.ValueKind != JsonValueKind.Number)
            throw new FuseraftCliException(
                $"The installed fuseraft doesn't report a protocol version. fuseraft-web needs a fuseraft that speaks protocol version {ProtocolVersion}; update it with 'fuseraft update'.");
        if (v.GetInt32() != ProtocolVersion)
            throw new FuseraftCliException(
                $"The installed fuseraft speaks protocol version {v.GetInt32()}, but this fuseraft-web speaks version {ProtocolVersion}. Install matching versions.");
    }

    private ProcessStartInfo StartInfo(IEnumerable<string> args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(Binary)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        if (Home is not null)
        {
            Directory.CreateDirectory(Home);
            psi.Environment["FUSERAFT_HOME"] = Home;
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> ExecAsync(string[] args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = StartInfo(args, workingDirectory);
        psi.RedirectStandardInput = false;
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            throw new FuseraftCliException($"Could not run '{Binary}': {ex.Message}. Set Fuseraft:Binary in appsettings.json to its path.");
        }
        using (proc)
        {
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode, await stdout, await stderr);
        }
    }

    private static string? FirstNonEmpty(params string[] texts) =>
        texts.Select(t => t.Trim()).FirstOrDefault(t => t.Length > 0) is { } t
            ? t.Split('\n').Last().Trim()
            : null;

    private sealed class BridgeProcess : IBridgeProcess
    {
        private const int StderrLines = 20;
        private readonly Process _proc;
        private readonly Queue<string> _stderr = new();

        public BridgeProcess(Process proc)
        {
            _proc = proc;
            _proc.StandardInput.AutoFlush = true;
            // Approval answers and user input are written from different threads; lines mustn't interleave.
            Input = TextWriter.Synchronized(_proc.StandardInput);
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (_stderr)
                {
                    _stderr.Enqueue(e.Data);
                    if (_stderr.Count > StderrLines) _stderr.Dequeue();
                }
            };
            _proc.BeginErrorReadLine();
        }

        public TextReader Output => _proc.StandardOutput;
        public TextWriter Input  { get; }

        public string StderrTail
        {
            get { lock (_stderr) return string.Join('\n', _stderr); }
        }

        public async Task<int> WaitForExitAsync()
        {
            await _proc.WaitForExitAsync();
            return _proc.ExitCode;
        }

        public void Kill()
        {
            try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        public void Dispose() => _proc.Dispose();
    }
}
