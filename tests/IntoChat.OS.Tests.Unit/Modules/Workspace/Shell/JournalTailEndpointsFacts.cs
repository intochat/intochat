using System.Net;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Units;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The debugger's journal tail route is the framework's route (mapped by MapModuleHttpSurfaces, which the os host calls).
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
