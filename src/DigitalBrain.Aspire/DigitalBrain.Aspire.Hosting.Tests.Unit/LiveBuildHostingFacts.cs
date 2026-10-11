using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;
using DigitalBrain.Aspire.Testing;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.Orleans;

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

        var model = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>((await ((IValueProvider)environment[AspireModuleHosting.CandidateModelVariable]).GetValueAsync(TestContext.Current.CancellationToken))!)!;
        Assert.Equal(new Dictionary<string, string> { [AspireModuleHosting.IntegrationVariable("openrouter", "ApiKey")] = "model-key" }, model);
    }

    // The candidate host reads the live grain state from the same orleans integration the silo stores in.
    [Fact]
    public async Task TheAspireIntegrationCarriesTheLiveGrainStateOfTheOrleansIntegration()
    {
        var builder = AspireCompositionTest.CreateBuilder();
        var brain = builder.AddDigitalBrain("modules").WithHosting<AspireModuleHosting>();
        var silo = builder.AddExecutable("silo", "unused", ".")
            .WithEnvironment(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.IntegrationEnvironmentName("orleans", OrleansStorageHosting.GrainStateField), "UseDevelopmentStorage=true")
            .WithReference(brain);

        var environment = await AspireCompositionTest.EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);

        Assert.Equal("UseDevelopmentStorage=true", environment[AspireModuleHosting.LiveGrainStateVariable]);
    }

    // Each role runs two named instances on dynamic ports; a YARP front owns the fixed port, routes everything to
    // both instances and health-checks them, so a restarting instance leaves the port served.
    [Theory]
    [InlineData("OperatingSystem", 5080)]
    [InlineData("digitalbrain-mcp", 5081)]
    public async Task TheSiloRunsTwoReplicasBehindTheFixedPorts(string role, int port)
    {
        SetLive("abc1234");
        var builder = AspireCompositionTest.CreateBuilder();
        var instances = AspireModuleHosting.InstanceNames(role)
            .Select(name => AspireModuleHosting.OnDynamicPorts(AspireModuleHosting.AddLiveBuild(builder, workdir, name, "os", "DigitalBrain.OS.dll")!, "ASPNETCORE_HTTP_PORTS"))
            .ToArray();

        var front = AspireModuleHosting.AddFront(builder, role, port, instances.Select(instance => instance.GetEndpoint("http")));

        Assert.Equal([role + "-a", role + "-b"], instances.Select(instance => instance.Resource.Name));
        // AspireCliClusterHost recognises an instance as {role}-{one lowercase letter}.
        Assert.All(AspireModuleHosting.Instances, instance => Assert.True(instance is [var letter] && char.IsAsciiLetterLower(letter)));
        Assert.All(instances, instance => Assert.Null(Assert.Single(instance.Resource.Annotations.OfType<EndpointAnnotation>()).Port));
        Assert.Equal(role + "-front", front.Resource.Name);
        Assert.Equal(port, Assert.Single(front.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http").Port);
        using var app = builder.Build();
        var environment = await AspireCompositionTest.EnvironmentOf(builder, front.Resource, TestContext.Current.CancellationToken);
        var cluster = $"REVERSEPROXY__CLUSTERS__cluster_{role}__";
        Assert.Equal("/{**catch-all}", environment["REVERSEPROXY__ROUTES__route0__MATCH__PATH"]);
        Assert.Equal($"cluster_{role}", environment["REVERSEPROXY__ROUTES__route0__CLUSTERID"]);
        Assert.Equal($"http://{role}-a", environment[cluster + "DESTINATIONS__destination1__ADDRESS"]);
        Assert.Equal($"http://{role}-b", environment[cluster + "DESTINATIONS__destination2__ADDRESS"]);
        Assert.Equal("/ready", environment[cluster + "HEALTHCHECK__ACTIVE__PATH"]);
        Assert.Equal("00:00:01", environment[cluster + "HEALTHCHECK__ACTIVE__INTERVAL"]);
        // The front answers its own /ready from whichever instance it routes to, on that instance's /alive: a
        // draining instance's 503 is not the front's.
        Assert.Equal("/ready", environment["REVERSEPROXY__ROUTES__route1__MATCH__PATH"]);
        Assert.Equal($"cluster_{role}", environment["REVERSEPROXY__ROUTES__route1__CLUSTERID"]);
        Assert.Equal("/alive", environment["REVERSEPROXY__ROUTES__route1__TRANSFORMS__0__PathSet"]);
        // One route per instance, through a cluster of that instance alone with the same health check: a roll
        // sees the instance as the front does.
        foreach (var (instance, index) in instances.Select((instance, index) => (instance.Resource.Name, index)))
        {
            var route = $"REVERSEPROXY__ROUTES__route{index + 2}__";
            Assert.Equal($"/_instances/{instance}/ready", environment[route + "MATCH__PATH"]);
            Assert.Equal($"cluster_{instance}", environment[route + "CLUSTERID"]);
            Assert.Equal("/alive", environment[route + "TRANSFORMS__0__PathSet"]);
            Assert.Equal($"http://{instance}", environment[$"REVERSEPROXY__CLUSTERS__cluster_{instance}__DESTINATIONS__destination1__ADDRESS"]);
            Assert.Equal("/ready", environment[$"REVERSEPROXY__CLUSTERS__cluster_{instance}__HEALTHCHECK__ACTIVE__PATH"]);
            Assert.Equal("00:00:01", environment[$"REVERSEPROXY__CLUSTERS__cluster_{instance}__HEALTHCHECK__ACTIVE__INTERVAL"]);
            Assert.Equal("HealthyAndUnknown", environment[$"REVERSEPROXY__CLUSTERS__cluster_{instance}__HEALTHCHECK__AVAILABLEDESTINATIONSPOLICY"]);
        }
        Assert.Contains($"services__{role}-a__http__0", environment.Keys);
        Assert.Contains($"services__{role}-b__http__0", environment.Keys);
        foreach (var instance in instances)
        {
            var drain = await AspireCompositionTest.EnvironmentOf(builder, instance.Resource, TestContext.Current.CancellationToken);
            Assert.Equal(AspireModuleHosting.DrainDelay.ToString("c"), drain[AspireModuleHosting.DrainDelayVariable]);
        }
    }

    // A roll reaches each instance through its role's front: the silos learn every front's fixed port.
    [Fact]
    public async Task TheAspireIntegrationNamesEachRolesFront()
    {
        var builder = AspireCompositionTest.CreateBuilder();
        var brain = builder.AddDigitalBrain("modules").WithHosting<AspireModuleHosting>();
        var silo = builder.AddExecutable("silo", "unused", ".").WithReference(brain);
        var mcp = AspireModuleHosting.OnDynamicPorts(builder.AddExecutable("digitalbrain-mcp-a", "unused", "."), "ASPNETCORE_HTTP_PORTS");
        AspireModuleHosting.AddFront(builder, "OperatingSystem", 5080, [silo.WithHttpEndpoint(name: "http").GetEndpoint("http")]);
        AspireModuleHosting.AddFront(builder, "digitalbrain-mcp", 5081, [mcp.GetEndpoint("http")]);

        var environment = await AspireCompositionTest.EnvironmentOf(builder, silo.Resource, TestContext.Current.CancellationToken);

        var fronts = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>((string)environment[AspireModuleHosting.FrontsVariable])!;
        Assert.Equal(new Dictionary<string, string> { ["OperatingSystem"] = "http://localhost:5080", ["digitalbrain-mcp"] = "http://localhost:5081" }, fronts);
    }

    // A launch profile's applicationUrl pins ports; two instances of one project would share them.
    [Fact]
    public void AnInstanceDropsThePortsItsProfilePinned()
    {
        var instance = AspireCompositionTest.CreateBuilder().AddExecutable("OperatingSystem-a", "dotnet", ".")
            .WithHttpEndpoint(port: 5310, name: "http").WithHttpsEndpoint(port: 60275, name: "https");

        AspireModuleHosting.OnDynamicPorts(instance);

        Assert.All(instance.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint =>
        {
            Assert.Null(endpoint.Port);
            Assert.False(endpoint.IsProxied);
        });
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
