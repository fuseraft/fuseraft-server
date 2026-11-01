using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using fuseraft.Web.Services.Cli;
using fuseraft.Web.Services.Users;

namespace FuseraftWeb.Tests;

/// <summary>A <see cref="FuseraftCli"/> that answers queries from canned JSON and hands out scripted bridges.</summary>
internal sealed class FakeCli() : FuseraftCli(Options.Create(new FuseraftCliOptions()))
{
    public Dictionary<string, string> Responses { get; } = [];
    public List<string[]> Runs { get; } = [];
    public Queue<FakeBridge> Bridges { get; } = new();
    public List<(string[] Args, string? Cwd)> Started { get; } = [];

    public List<(string[] Args, string? Cwd)> Queries { get; } = [];
    public List<IReadOnlyDictionary<string, string>?> Environments { get; } = [];
    /// <summary>Every home a caller asked for; the fake answers for all of them.</summary>
    public List<string> Homes { get; } = [];

    public override FuseraftCli ForHome(string home)
    {
        Homes.Add(home);
        return this;
    }

    protected override Task<JsonDocument> QueryAsync(string[] args, string? workingDirectory = null)
    {
        Queries.Add((args, workingDirectory));
        return Responses.TryGetValue(string.Join(' ', args), out var json)
            ? Task.FromResult(JsonDocument.Parse(json))
            : throw new FuseraftCliException($"no canned response for '{string.Join(' ', args)}'");
    }

    public override Task RunAsync(params string[] args) => RunAsync(null, args);

    public override Task RunAsync(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        Runs.Add(args);
        return Task.CompletedTask;
    }

    public List<fuseraft.Web.Services.Relay.BridgeTag?> Tags { get; } = [];
    /// <summary>What <see cref="ReattachBridgesAsync"/> finds: processes "left running by a previous server".</summary>
    public List<(fuseraft.Web.Services.Relay.BridgeTag Tag, IBridgeProcess Process)> Running { get; } = [];

    public override Task<IBridgeProcess> StartBridgeAsync(IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null, fuseraft.Web.Services.Relay.BridgeTag? tag = null)
    {
        Tags.Add(tag);
        return Task.FromResult(StartBridge(args, workingDirectory, environment));
    }

    public override Task<IReadOnlyList<(fuseraft.Web.Services.Relay.BridgeTag Tag, IBridgeProcess Process)>> ReattachBridgesAsync() =>
        Task.FromResult<IReadOnlyList<(fuseraft.Web.Services.Relay.BridgeTag, IBridgeProcess)>>([.. Running]);

    public override IBridgeProcess StartBridge(IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Started.Add(([.. args], workingDirectory));
        Environments.Add(environment);
        return Bridges.Dequeue();
    }
}

/// <summary>A bridge process whose stdout the test writes line by line and whose stdin it reads back.</summary>
internal sealed class FakeBridge : IBridgeProcess
{
    private readonly Channel<string?> _out = Channel.CreateUnbounded<string?>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringWriter _in = new();

    public FakeBridge()
    {
        Output = new ChannelReader(_out.Reader);
        Input  = TextWriter.Synchronized(_in);
    }

    public TextReader Output { get; }
    // The synchronized wrapper locks on itself, so InputLines takes the same lock to read.
    public TextWriter Input  { get; }
    public string StderrTail { get; set; } = "";
    public bool Killed { get; private set; }

    /// <summary>Every line written to the process's stdin so far.</summary>
    public string[] InputLines
    {
        get { lock (Input) return _in.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
    }

    public FakeBridge Send(object evt)
    {
        _out.Writer.TryWrite(JsonSerializer.Serialize(evt));
        return this;
    }

    public FakeBridge SendRaw(string line)
    {
        _out.Writer.TryWrite(line);
        return this;
    }

    /// <summary>Closes stdout and exits with <paramref name="code"/>.</summary>
    public void Exit(int code)
    {
        _out.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public Task<int> WaitForExitAsync() => _exit.Task;

    public void Kill()
    {
        Killed = true;
        Exit(-1);
    }

    public void Dispose() { }

    private sealed class ChannelReader(ChannelReader<string?> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await lines.WaitToReadAsync(cancellationToken) && lines.TryRead(out var line) ? line : null;

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; the work under test runs on background tasks.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(10);
        }
    }
}

/// <summary>A <see cref="UserStore"/> in a temp directory whose accounts' homes are temp directories too.</summary>
internal sealed class Accounts : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fuseraft-web-tests").FullName;

    public Accounts()
    {
        Store = new UserStore(
            Options.Create(new fuseraft.Web.Services.Users.WebOptions { DataDirectory = _dir }),
            Options.Create(new fuseraft.Web.Services.Users.AuthOptions()));
        Alice = Add("alice");
    }

    public UserStore Store { get; }
    /// <summary>The first account, so an admin.</summary>
    public WebUser Alice { get; }

    public WebUser Add(string name, List<string>? roots = null)
    {
        var user = Store.Create(name, password: null, isAdmin: false);
        // The first account would get the real ~/.fuseraft; keep every test away from it.
        Store.Update(user.Id, u =>
        {
            u.Home = Path.Combine(_dir, "homes", name);
            if (roots is not null) u.WorkspaceRoots = roots;
        });
        return user;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
