using fuseraft.Server.Models;
using fuseraft.Server.Services;
using fuseraft.Server.Services.Daemons;

namespace FuseraftServer.Tests;

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
}
