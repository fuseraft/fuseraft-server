using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace fuseraft.Web.Services.Relay;

/// <summary>Starts relays under <c>&lt;data&gt;/relays/</c>, and finds the ones a previous server left running.</summary>
public sealed class RelayLauncher(string dataDirectory)
{
    private string RelaysDir => Path.Combine(dataDirectory, "relays");

    internal static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Runs <paramref name="command"/> in a relay of its own and connects to it.</summary>
    public async Task<RelayBridge> StartAsync(string command, IEnumerable<string> args, string? cwd,
        IReadOnlyDictionary<string, string>? environment, BridgeTag tag)
    {
        var id  = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var dir = Path.Combine(RelaysDir, id);
        Directory.CreateDirectory(dir);
        var meta = new RelayMeta
        {
            Command = command, Args = [.. args], Cwd = cwd, SocketPath = SocketPath(id), Tag = tag,
        };
        meta.Write(dir);

        // Secrets (profile keys) travel only in the environment, never in meta.json.
        var psi = SelfStartInfo(dir);
        foreach (var (k, v) in environment ?? new Dictionary<string, string>()) psi.Environment[k] = v;
        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Directory.Delete(dir, recursive: true);
            throw new Cli.FuseraftCliException($"Could not start a relay for '{command}': {ex.Message}");
        }

        return await RelayBridge.ConnectAsync(dir, meta.SocketPath, ConnectTimeout)
               ?? throw new Cli.FuseraftCliException($"The relay for '{command}' didn't start. Its files are in {dir}.");
    }

    /// <summary>
    /// Connects to every relay still running, for a server that just started. A relay that isn't running had its
    /// process end while no server was connected; fuseraft's own history has the outcome, so its files are removed.
    /// </summary>
    public async Task<List<(BridgeTag Tag, RelayBridge Bridge)>> ReattachAsync()
    {
        var found = new List<(BridgeTag, RelayBridge)>();
        if (!Directory.Exists(RelaysDir)) return found;
        foreach (var dir in Directory.GetDirectories(RelaysDir))
        {
            RelayMeta meta;
            try { meta = RelayMeta.Read(dir); }
            catch { TryDelete(dir); continue; }

            if (await RelayBridge.ConnectAsync(dir, meta.SocketPath) is { } bridge)
                found.Add((meta.Tag, bridge));
            else
                TryDelete(dir);
        }
        return found;
    }

    /// <summary>
    /// Sockets live outside the data directory: a Unix socket path must stay under about 100 bytes, and a data
    /// directory can be deeper than that allows.
    /// </summary>
    private static string SocketPath(string id)
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var dir = string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), $"fuseraft-web-{Environment.UserName}")
            : Path.Combine(runtime, "fuseraft-web");
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Path.Combine(dir, $"{id}.sock");
    }

    /// <summary>This server's own binary, run as <c>relay &lt;dir&gt;</c>; under <c>dotnet fuseraft-web.dll</c> that's dotnet plus the dll.</summary>
    private static ProcessStartInfo SelfStartInfo(string dir)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell which binary is running.");
        var psi  = new ProcessStartInfo(self)
        {
            UseShellExecute  = false,
            CreateNoWindow   = true,
            WorkingDirectory = dir,
        };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        psi.ArgumentList.Add("relay");
        psi.ArgumentList.Add(dir);
        return psi;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
