using fuseraft.Server.Services;

namespace FuseraftServer.Tests;

public sealed class OrchestrationTemplateServiceTests
{
    private const string Result =
        """{"protocolVersion":1,"item":{"template":"solo","model":"gpt-4o","configPath":"/w/.fuseraft/config/o.yaml","agentFiles":[]}}""";

    [Fact]
    public async Task Generate_RunsInitInTheWorkspace()
    {
        var cli = new FakeCli();
        cli.Responses["init o.yaml --template solo --json --model gpt-4o --force"] = Result;

        var result = await new OrchestrationTemplateService(cli).GenerateAsync("/w", "solo", "o.yaml", "gpt-4o", "", force: true);

        Assert.Equal("/w/.fuseraft/config/o.yaml", result.ConfigPath);
        Assert.Equal("/w", Assert.Single(cli.Queries).Cwd);
    }

    [Fact]
    public async Task Generate_WithoutModel_LetsInitDetectIt()
    {
        var cli = new FakeCli();
        cli.Responses["init o.yaml --template solo --json"] = Result;

        await new OrchestrationTemplateService(cli).GenerateAsync("/w", "solo", "o.yaml", model: " ", endpoint: null, force: false);

        Assert.DoesNotContain("--model", Assert.Single(cli.Queries).Args);
    }
}
