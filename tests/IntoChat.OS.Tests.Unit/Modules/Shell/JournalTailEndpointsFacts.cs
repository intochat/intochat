using System.Net;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The debugger's reads at the edge: the journal tail is the framework's route (mapped by MapModuleHttpSurfaces, which the os host
// calls), the state and view reads and the topology snapshot are the module's.
public sealed class JournalTailEndpointsFacts
{
    private const string Brain = "owner";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ISynapseCatalogSource Catalog = new SynapseCatalogSource(SynapseCatalog.Scan(typeof(CounterUnit).Assembly));

    private static async Task<TestOS> UnitOnTestOS()
    {
        var os = TestOS.Local().Register(typeof(CounterUnit).Assembly);
        os.Confirmations.ApproveAll();
        await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).Install(new InstallRequest("ctr", new Dictionary<string, string>()), Ct);
        await os.Idle();
        await os.Get<IKicker>(NeuronId.Parse("owner/kicker/a")).Kick();
        await os.Idle();
        return os;
    }

    [Fact]
    public async Task TheStateOfAUnitIsItsCurrentRecord()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();

        var result = await UnitReadEndpoints.State(new TestOSBrain(os), Brain, Catalog, "ctr", Ct);

        var state = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(1, state.GetProperty("kicks").GetInt32());
    }

    [Fact]
    public async Task TheViewOfAUnitCarriesItsGeneration()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();

        var result = await UnitReadEndpoints.View(new TestOSBrain(os), Brain, Catalog, "ctr", Ct);

        var view = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<IValueHttpResult>(result).Value, JsonSerializerOptions.Web);
        Assert.Equal("ctr", view.GetProperty("alias").GetString());
        Assert.True(view.GetProperty("generation").GetInt64() >= 1);
        Assert.Equal(JsonValueKind.Object, view.GetProperty("view").ValueKind);
    }

    [Fact]
    public async Task AnUnknownAliasIs404AndAnAliasThatAddressesAnotherNeuronIs400()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();
        var brain = new TestOSBrain(os);

        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await UnitReadEndpoints.State(brain, Brain, Catalog, "nobody", Ct)).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await UnitReadEndpoints.View(brain, Brain, Catalog, "nobody", Ct)).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await UnitReadEndpoints.State(brain, Brain, Catalog, "ctr/other", Ct)).StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await UnitReadEndpoints.View(brain, Brain, Catalog, null, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheRoutesAnswerAMemberAndRefuseAnyoneElse()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();
        var access = new Access();
        await using var app = await Start(os, access);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var refusedState = await client.GetAsync("/brains/owner/state?alias=ctr", Ct);
        var refusedView = await client.GetAsync("/brains/owner/view?alias=ctr", Ct);
        access.Allowed = true;
        var state = await client.GetAsync("/brains/owner/state?alias=ctr", Ct);
        var view = await client.GetAsync("/brains/owner/view?alias=ctr", Ct);
        var unknown = await client.GetAsync("/brains/owner/state?alias=nobody", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refusedState.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refusedView.StatusCode);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await state.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("kicks").GetInt32());
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task TheFrameworksTailRouteIsMappedBehindASession()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();
        await using var app = await Start(os, new Access { Allowed = true });
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Select(route => route.RoutePattern.RawText).ToArray();
        var anonymous = await client.GetAsync("/brains/owner/journal/tail", Ct);

        Assert.Contains("/brains/{brainId}/journal/tail", routes);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task ATopologyHasNodesAndEdgesAndNoActivity()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = await UnitOnTestOS();
        var installed = (await os.Get<IInstalls>(NeuronId.Parse("owner/synapse.installs/default")).List(Ct)).Select(record => record.Alias).ToArray();
        var catalog = SynapseCatalog.Scan(typeof(CounterUnit).Assembly);

        var (json, _) = BrainEventsEndpoints.Topology(Brain, [("owner/kicker/a", "kicker")], installed, catalog, DateTimeOffset.UnixEpoch);

        using var snapshot = JsonDocument.Parse(json);
        var root = snapshot.RootElement;
        Assert.False(root.TryGetProperty("activity", out _));
        Assert.False(root.TryGetProperty("correlations", out _));
        var nodes = root.GetProperty("nodes").EnumerateArray().ToDictionary(node => node.GetProperty("id").GetString()!);
        Assert.Equal("observed", nodes["owner/kicker/a"].GetProperty("role").GetString());
        Assert.Equal("unit", nodes["owner/ctr/default"].GetProperty("role").GetString());
        var edges = root.GetProperty("synapses").EnumerateArray().ToArray();
        Assert.Contains(edges, edge => edge.GetProperty("signalType").GetString() == "ctr.kick" && edge.GetProperty("targetId").GetString() == "owner/ctr/default"
            && edge.GetProperty("kind").GetString() == "reaction");
        Assert.Contains(edges, edge => edge.GetProperty("signalType").GetString() == "ctr.bump");
    }

    [Fact]
    public void ANeuronTheKernelListsIsNotDrawnTwiceAndAnUnchangedBrainHasTheSameKey()
    {
        var catalog = SynapseCatalog.Scan(typeof(CounterUnit).Assembly);
        (string, string)[] kernel = [("owner/ctr/default", "ctr")];

        var (first, key) = BrainEventsEndpoints.Topology(Brain, kernel, ["ctr"], catalog, DateTimeOffset.UnixEpoch);
        var (_, later) = BrainEventsEndpoints.Topology(Brain, kernel, ["ctr"], catalog, DateTimeOffset.UnixEpoch.AddHours(1));

        using var snapshot = JsonDocument.Parse(first);
        Assert.Single(snapshot.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.Equal(key, later);
    }

    private static async Task<WebApplication> Start(TestOS os, Access access)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(JournalTailEndpointsFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IBrainAccess>(access);
        builder.Services.AddSingleton<INeuronProxies>(new TestOSBrain(os));
        builder.Services.AddSingleton(Catalog);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Callers);
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            var caller = IntoChat.OS.Tests.TestEdge.Authority.Person("ada", "owner", new BrainId(Brain));
            using (IntoChat.OS.Tests.TestEdge.Authority.Enter(caller))
            {
                await next(http);
            }
        });
        UnitReadEndpoints.Map(app);
        app.MapModuleHttpSurfaces();
        await app.StartAsync(Ct);
        return app;
    }

    private sealed class Access : IBrainAccess
    {
        public bool Allowed { get; set; }

        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Allowed);
    }

    private sealed class TestOSBrain(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }
}
