using System.Net;
using System.Security.Cryptography;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Orleans;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ServerRecipeFacts
{
    [Fact]
    public void ADuplicateModuleIdFailsNamingTheModule()
    {
        var server = new DigitalBrainServerBuilder().AddModule<TimeModule>();
        var error = Assert.Throws<InvalidOperationException>(server.AddModule<TimeModule>);
        Assert.Contains("time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecipeHostsASiloAndTheHttpSurfaceInOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DigitalBrainNames.MasterKeyConfigurationKey] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [$"{PartsOptions.Key}:MachineDirectory"] = Directory.CreateTempSubdirectory("recipe-").FullName,
        });
        builder.AddDigitalBrainServer(server => server.AddModule<OrleansModule>().AddModule<TimeModule>());
        await using var app = builder.Build();
        app.MapDigitalBrainServer();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var ready = await http.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(BrainHosting.HasKernelRegistration(app.Services));
        Assert.Throws<InvalidOperationException>(app.MapDigitalBrainServer);
        await app.StopAsync(ct);
    }
}
