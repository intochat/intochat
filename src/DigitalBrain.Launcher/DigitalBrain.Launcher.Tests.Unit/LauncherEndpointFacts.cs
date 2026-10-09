using System.Net;
using System.Net.Http.Json;
using DigitalBrain.Launcher.Processes;
using DigitalBrain.Launcher.Proxy;
using DigitalBrain.Launcher.Revisions;
using DigitalBrain.Launcher.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using LauncherRevisions = DigitalBrain.Launcher.Revisions.Revisions;

namespace DigitalBrain.Launcher.Tests.Unit;

public sealed class LauncherEndpointFacts
{
    [Fact]
    public async Task TheApiProposesStartsPromotesAndListsRevisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("launcher-api-").FullName;
        var processes = new FakeProcessRunner { Answer = (_, args) => args[0] == "rev-parse" ? new(0, "abc1234\n", "") : new(0, "", "") };
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(new LauncherOptions { Repository = root, RevisionsPath = Path.Combine(root, "revisions.json") });
        builder.Services.AddSingleton<IProcessRunner>(processes);
        builder.Services.AddSingleton<IStorageCopier>(new FakeStorageCopier());
        builder.Services.AddSingleton(new HealthProbe(_ => Task.FromResult(HttpStatusCode.OK)));
        builder.Services.AddRevisionProxy(5080, 5081);
        builder.Services.AddSingleton<LauncherRevisions>();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapLauncher();
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var proposed = await client.PostAsJsonAsync("/revisions", new { branch = "os-runtime", summary = "change", files = new Dictionary<string, string>() }, ct);
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        Assert.Equal("abc1234", (await proposed.Content.ReadFromJsonAsync<Revision>(ct))!.Sha);
        Assert.Equal("starting", (await (await client.PostAsync("/revisions/abc1234/start", null, ct)).Content.ReadFromJsonAsync<Revision>(ct))!.State);
        Assert.Equal("healthy", (await (await client.PostAsync("/revisions/abc1234/healthy?budget=00:00:05", null, ct)).Content.ReadFromJsonAsync<Revision>(ct))!.State);
        Assert.Equal("current", (await (await client.PostAsync("/revisions/abc1234/promote", null, ct)).Content.ReadFromJsonAsync<Revision>(ct))!.State);
        Assert.Equal("abc1234", (await client.GetFromJsonAsync<RevisionsDocument>("/revisions", ct))!.Current);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/revisions/nope/start", null, ct)).StatusCode);
        await app.StopAsync(ct);
        Directory.Delete(root, recursive: true);
    }
}
