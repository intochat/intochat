using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using DigitalBrain.Aspire.Testing;
using DigitalBrain.Microsoft.Aspire;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

// The live silos run the published build {Workdir}/live.json names; without one the AppHost runs from source.
public sealed class LiveBuildHostingFacts : IDisposable
{
    private readonly string workdir = Directory.CreateTempSubdirectory("live-build-").FullName;

    public void Dispose() => Directory.Delete(workdir, recursive: true);

    [Fact]
    public async Task TheSiloRunsTheLiveBuildWhenOneIsSet()
    {
        SetLive("abc1234");

        var silo = AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll");

        Assert.NotNull(silo);
        Assert.Equal("dotnet", silo.Resource.Command);
        Assert.Equal([Path.Combine(workdir, "builds", "abc1234", "os", "DigitalBrain.OS.dll")], await ArgumentsOf(silo.Resource));
        var environment = await AspireCompositionTest.EnvironmentOf(AspireCompositionTest.CreateBuilder(), silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(workdir, "builds", "abc1234", "os"), environment[AspireModuleHosting.AspNetCoreContentRoot]);
    }

    [Fact]
    public async Task ARestartReadsTheBuildSetSince()
    {
        SetLive("abc1234");
        var silo = AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll")!;

        SetLive("def5678");

        Assert.Equal([Path.Combine(workdir, "builds", "def5678", "os", "DigitalBrain.OS.dll")], await ArgumentsOf(silo.Resource));
        var environment = await AspireCompositionTest.EnvironmentOf(AspireCompositionTest.CreateBuilder(), silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(workdir, "builds", "def5678", "os"), environment[AspireModuleHosting.DotnetContentRoot]);
    }

    // The live silo's apps are the ones published with its build, never the working tree's.
    [Fact]
    public async Task TheSiloReadsTheFeaturesPublishedWithItsBuild()
    {
        SetLive("abc1234");
        var features = Path.Combine(workdir, "builds", "abc1234", "os", AspireModuleHosting.FeaturesFolder);
        Directory.CreateDirectory(features);

        var silo = AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll")!;

        var environment = await AspireCompositionTest.EnvironmentOf(AspireCompositionTest.CreateBuilder(), silo.Resource, TestContext.Current.CancellationToken);
        Assert.Equal(features, environment[AspireModuleHosting.FeaturesVariable]);
    }

    // The candidate host's only view of the live deployment: the model Integrations' values, packed into the
    // aspire Integration. No other Integration of the silo's environment is handed over.
    [Fact]
    public async Task TheAspireIntegrationCarriesTheModelsAndNoOtherIntegration()
    {
        var builder = AspireCompositionTest.CreateBuilder();
        var brain = builder.AddDigitalBrain("modules").WithHosting<AspireModuleHosting>();
        var silo = builder.AddExecutable("silo", "unused", ".")
            .WithEnvironment(AspireModuleHosting.IntegrationVariable("openrouter", "ApiKey"), "model-key")
            .WithEnvironment(AspireModuleHosting.IntegrationVariable("github", "PrivateKeyPem"), "pem")
            .WithReference(brain);

        var environment = await AspireCompositionTest.EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);

        var model = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>((string)environment[AspireModuleHosting.CandidateModelVariable])!;
        Assert.Equal(new Dictionary<string, string> { [AspireModuleHosting.IntegrationVariable("openrouter", "ApiKey")] = "model-key" }, model);
    }

    [Fact]
    public void TheSiloRunsTwoReplicasBehindTheFixedPorts()
    {
        SetLive("abc1234");
        var builder = AspireCompositionTest.CreateBuilder();
        var source = AspireModuleHosting.Replicated(builder.AddExecutable("source", "dotnet", "."), 5080);
        var live = AspireModuleHosting.Replicated(AspireModuleHosting.AddLiveBuild(builder, workdir, "digitalbrain-mcp", "os", "DigitalBrain.Mcp.dll")!, 5081, "ASPNETCORE_HTTP_PORTS");

        foreach (var (resource, port) in new[] { (source.Resource, 5080), (live.Resource, 5081) })
        {
            Assert.Equal(2, Assert.Single(resource.Annotations.OfType<ReplicaAnnotation>()).Replicas);
            var http = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http");
            Assert.Equal(port, http.Port);
            Assert.True(http.IsProxied);
        }
    }

    [Fact]
    public void ALiveBuildThatIsGoneFallsBackToSource()
    {
        SetLive("abc1234");
        Directory.Delete(Path.Combine(workdir, "builds"), recursive: true);

        Assert.Null(AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll"));
    }

    [Fact]
    public void WithoutALiveBuildTheAppHostRunsFromSource()
        => Assert.Null(AspireModuleHosting.AddLiveBuild(AspireCompositionTest.CreateBuilder(), workdir, "OperatingSystem", "os", "DigitalBrain.OS.dll"));

    private void SetLive(string sha)
    {
        var build = Path.Combine(workdir, "builds", sha);
        Directory.CreateDirectory(Path.Combine(build, "os"));
        File.WriteAllText(Path.Combine(workdir, "live.json"),
            System.Text.Json.JsonSerializer.Serialize(new { sha, path = build }));
    }

    private static async Task<string[]> ArgumentsOf(IResource resource)
    {
        var args = new List<object>();
        foreach (var callback in resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await callback.Callback(new CommandLineArgsCallbackContext(args, resource, TestContext.Current.CancellationToken));
        }
        return [.. args.Select(arg => arg.ToString()!)];
    }
}
