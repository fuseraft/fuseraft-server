using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using fuseraft.Server.Services.Cli;

namespace FuseraftServer.Tests;

/// <summary>A <see cref="FuseraftCli"/> that answers queries from canned JSON and hands out scripted bridges.</summary>
internal sealed class FakeCli() : FuseraftCli(Options.Create(new FuseraftCliOptions()))
{
    public Dictionary<string, string> Responses { get; } = [];
    public List<string[]> Runs { get; } = [];
    public Queue<FakeBridge> Bridges { get; } = new();
    public List<(string[] Args, string? Cwd)> Started { get; } = [];

    public List<(string[] Args, string? Cwd)> Queries { get; } = [];

    protected override Task<JsonDocument> QueryAsync(string[] args, string? workingDirectory = null)
    {
        Queries.Add((args, workingDirectory));
        return Responses.TryGetValue(string.Join(' ', args), out var json)
            ? Task.FromResult(JsonDocument.Parse(json))
            : throw new FuseraftCliException($"no canned response for '{string.Join(' ', args)}'");
    }

    public override Task RunAsync(params string[] args)
    {
        Runs.Add(args);
        return Task.CompletedTask;
    }

    public override IBridgeProcess StartBridge(IEnumerable<string> args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Started.Add(([.. args], workingDirectory));
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
