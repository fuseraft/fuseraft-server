using Microsoft.AspNetCore.DataProtection;
using fuseraft.Web.Models;
using fuseraft.Web.Services;
using fuseraft.Web.Services.Daemons;

namespace FuseraftWeb.Tests;

public sealed class DaemonServiceTests
{
    private static DaemonService Service(params DaemonEndpoint[] configured) =>
        new(configured, new HitlBroker(), () => throw new InvalidOperationException("no transport in these tests"));

    [Fact]
    public void Configured_SkipsInvalidAndDuplicateEntries()
    {
        var service = Service(
            new DaemonEndpoint { Name = "a", Url = "ws://a:1/attach" },
            new DaemonEndpoint { Name = "A", Url = "ws://dup:1/attach" },
            new DaemonEndpoint { Name = "b", Url = "http://b:1/attach" },
            new DaemonEndpoint { Name = "", Url = "ws://c:1/attach" });

        var only = Assert.Single(service.Connections);
        Assert.Equal(("a", true), (only.Endpoint.Name, only.Configured));
    }

    [Theory]
    [InlineData("", "ws://x:1/attach", "Name is required")]
    [InlineData("x", "http://x:1/attach", "ws://")]
    [InlineData("x", "not a url", "ws://")]
    [InlineData("A", "ws://x:1/attach", "already exists")]
    public void Validate_ExplainsWhatIsWrong(string name, string url, string expected) =>
        Assert.Contains(expected, Service(new DaemonEndpoint { Name = "a", Url = "ws://a:1/attach" }).Validate(name, url));

    [Fact]
    public async Task Added_DaemonsCanBeRemoved_ConfiguredOnesStay()
    {
        var service = Service(new DaemonEndpoint { Name = "kept", Url = "ws://k:1/attach" });
        var added = service.Add(" mine ", " ws://m:1/attach ", "  ");

        Assert.Equal(("mine", "ws://m:1/attach", null), (added.Endpoint.Name, added.Endpoint.Url, added.Endpoint.Token));

        await service.RemoveAsync(added.Id);
        await service.RemoveAsync(service.Connections.Single().Id);

        Assert.Equal("kept", Assert.Single(service.Connections).Endpoint.Name);
    }

    [Theory]
    [InlineData("ws://x:1/attach", "3a:9f", "64 hex digits")]
    [InlineData("ws://x:1/attach", "3A9F0000000000000000000000000000000000000000000000000000000000AA", "wss://")]
    public void Validate_ChecksTheCertificateFingerprint(string url, string pin, string expected) =>
        Assert.Contains(expected, Service().Validate("x", url, pin));

    [Fact]
    public void Validate_AcceptsAFingerprintInEitherSpelling()
    {
        var hex = string.Concat(Enumerable.Repeat("ab", 32));
        var colons = string.Join(':', Enumerable.Repeat("AB", 32));

        Assert.Null(Service().Validate("x", "wss://x:1/attach", hex));
        Assert.Null(Service().Validate("x", "wss://x:1/attach", colons));
        Assert.Equal(WebSocketDaemonTransport.NormalizeFingerprint(hex), WebSocketDaemonTransport.NormalizeFingerprint(colons));
    }

    [Fact]
    public async Task AddedDaemons_AndEveryClientId_SurviveARestart()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var configured = new DaemonEndpoint { Name = "ci", Url = "wss://ci:1/attach" };

        var first = Stored(dir, protection, configured);
        var added = first.Add("mine", "wss://m:1/attach", "s3cret", "AB:" + string.Join(':', Enumerable.Repeat("CD", 31)));
        await first.DisconnectAsync(added);
        var ids = first.Connections.ToDictionary(c => c.Endpoint.Name, c => c.ClientId);

        var second = Stored(dir, protection, configured);

        var restored = second.Connections.Single(c => !c.Configured);
        Assert.Equal(("mine", "s3cret", false), (restored.Endpoint.Name, restored.Endpoint.Token, restored.Endpoint.AutoConnect));
        Assert.StartsWith("AB:CD", restored.Endpoint.CertificateSha256);
        Assert.Equal(ids, second.Connections.ToDictionary(c => c.Endpoint.Name, c => c.ClientId));
        Assert.DoesNotContain("s3cret", File.ReadAllText(dir.File("daemons.json")));
    }

    [Fact]
    public async Task Removing_ForgetsTheDaemonAndItsClientId()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var first = Stored(dir, protection);
        var added = first.Add("mine", "ws://m:1/attach", null);
        var oldId = added.ClientId;

        await first.RemoveAsync(added.Id);

        Assert.Empty(Stored(dir, protection).Connections);
        Assert.NotEqual(oldId, Stored(dir, protection).Add("mine", "ws://m:1/attach", null).ClientId);
    }

    [Fact]
    public void ATokenTheKeysCanNoLongerRead_IsDroppedNotFatal()
    {
        using var dir = new TempDir();
        Stored(dir, new EphemeralDataProtectionProvider()).Add("mine", "ws://m:1/attach", "s3cret");

        var restored = Stored(dir, new EphemeralDataProtectionProvider()).Connections.Single();

        Assert.Null(restored.Endpoint.Token);
    }

    [Fact]
    public void ADaemon_IsForAdminsAndTheUsersItLists()
    {
        var service = Service(
            new DaemonEndpoint { Name = "shared", Url = "ws://s:1/attach", Users = ["Alice"] },
            new DaemonEndpoint { Name = "private", Url = "ws://p:1/attach" });

        Assert.Equal(["shared"], service.For(User("alice")).Select(c => c.Endpoint.Name));
        Assert.Empty(service.For(User("bob")));
        Assert.Equal(2, service.For(User("root", admin: true)).Count);
    }

    [Fact]
    public void WhoMayUseAnAddedDaemon_SurvivesARestart()
    {
        using var dir = new TempDir();
        var protection = new EphemeralDataProtectionProvider();
        var first = Stored(dir, protection);

        first.SetUsers(first.Add("mine", "ws://m:1/attach", null), ["alice", " bob ", "ALICE", ""]);

        Assert.Equal(["alice", "bob"], Stored(dir, protection).Connections.Single().Endpoint.Users);
    }

    [Fact]
    public void AConfiguredDaemonsUsers_ComeFromConfiguration()
    {
        var service = Service(new DaemonEndpoint { Name = "ci", Url = "ws://ci:1/attach" });

        Assert.Throws<InvalidOperationException>(() => service.SetUsers(service.Connections.Single(), ["alice"]));
    }

    private static fuseraft.Web.Services.Users.WebUser User(string name, bool admin = false) =>
        new() { Id = name, UserName = name, Home = "/tmp", IsAdmin = admin };

    private static DaemonService Stored(TempDir dir, IDataProtectionProvider protection, params DaemonEndpoint[] configured) =>
        new(configured, new HitlBroker(), () => throw new InvalidOperationException("no transport in these tests"),
            new DaemonStore(dir.File("daemons.json"), protection));

    private sealed class TempDir : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("fuseraft-web-daemons-").FullName;

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
