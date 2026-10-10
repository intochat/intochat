using System.Net;
using DigitalBrain.Aspire.Server;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Framework.Tests.Unit;

public sealed class OrleansDashboardFacts
{
    [Fact]
    public async Task TheOrleansDashboardAnswersOnlyAuthenticatedCallersWhenTheServerOptsIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.AddDigitalBrainServer(server => server.UseOrleansDashboard());
        builder.UseOrleans(silo => silo.UseLocalhostClustering().AddMemoryGrainStorage(DigitalBrainNames.ClusterGrainStorage).AddKernelRegistration().UseInMemoryReminderService());
        builder.Services.AddSingleton<IBrainAccess, AllowEveryone>();
        builder.Services.AddSingleton<ICallFilterStage, AllowEverything>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapOrleansDashboard();
        await app.StartAsync(ct);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
            var anonymous = await http.GetAsync(OrleansDashboard.Path + "/", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/", ct)).StatusCode);
        }
        finally { await app.StopAsync(ct); }
    }

    [Fact]
    public async Task AServerWhoseDefaultStorageIsNeverWrappedRefusesToStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.AddDigitalBrainServer(_ => { });
        builder.UseOrleans(silo => silo.UseLocalhostClustering().AddMemoryGrainStorage(DigitalBrainNames.ClusterGrainStorage).UseInMemoryReminderService());
        builder.Services.AddSingleton<IBrainAccess, AllowEveryone>();
        builder.Services.AddSingleton<ICallFilterStage, AllowEverything>();
        await using var app = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(ct));

        Assert.Contains("kernel registration", error.Message, StringComparison.Ordinal);
    }

    private sealed class AllowEveryone : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class AllowEverything : ICallFilterStage
    {
        public ValueTask<CallDecision?> EvaluateAsync(CallRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<CallDecision?>(null);
    }
}
