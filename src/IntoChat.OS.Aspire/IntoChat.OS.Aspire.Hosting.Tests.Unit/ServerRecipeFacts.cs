using System.Net;
using System.Security.Cryptography;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.Orleans;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ServerRecipeFacts
{
    [Fact]
    public void ADuplicateModuleIdFailsNamingTheModule()
    {
        var server = new IntoChatOSServerBuilder().AddModule<TimeModule>();
        var error = Assert.Throws<InvalidOperationException>(server.AddModule<TimeModule>);
        Assert.Contains("time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRecipeHostsASiloAndTheHttpSurfaceInOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.MasterKeyConfigurationKey] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [IntoChatOSNames.HomeKey] = Directory.CreateTempSubdirectory("recipe-").FullName,
        });
        builder.AddIntoChatOSServer(server => server.AddModule<OrleansModule>().AddModule<TimeModule>());
        await using var app = builder.Build();
        app.MapIntoChatOSServer();
        await app.StartAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var ready = await http.GetAsync("/ready", ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(BrainHosting.HasKernelRegistration(app.Services));
        Assert.Throws<InvalidOperationException>(app.MapIntoChatOSServer);
        await app.StopAsync(ct);
    }
}
