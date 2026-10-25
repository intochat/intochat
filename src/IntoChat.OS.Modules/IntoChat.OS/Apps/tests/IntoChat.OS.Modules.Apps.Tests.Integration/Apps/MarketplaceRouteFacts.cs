using System.Net;
using System.Text.Json;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// The Marketplace over HTTP: a publisher publishes, another brain browses, installs, sees an
// update when the publisher publishes again, and updates — all under the brain route.
public sealed class MarketplaceRouteFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";

    [Trait("host", "own")]
    [Fact]
    public async Task PublishBrowseInstallAndUpdateAcrossTwoBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var intochat = await brain.PersonAsync("intochat", ct);
        var alice = await brain.PersonAsync("alice", ct);
        var source = brain.Get<IApp>(IApp.Key(intochat.BrainId, "echo"));
        using (intochat.AsCaller())
        {
            await source.Create(new("Echo", "echoes", [new("ask", "ask it")], [], [], Icon: "icon.svg"), Spec);
            await source.BeginBuild(null);
            await source.CompleteBuild(await source.CurrentBuildAttempt(),
                new() { ["synapses/echo.cs"] = "// v1", ["icon.svg"] = "<svg id=\"echo\"/>" }, null);
        }
        var aliceApps = $"/brains/{alice.BrainId}/apps";
        var market = $"/brains/{alice.BrainId}/marketplace";

        // Publish acts in the caller's own brain: alice has no echo to publish; intochat does.
        using var refused = await alice.Http.PostAsync($"{aliceApps}/echo/publish", null, ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var released = (await source.Read()).Version;
        using var published = await intochat.Http.PostAsync($"/brains/{intochat.BrainId}/apps/echo/publish", null, ct);
        Assert.Equal(released, (await Json(published, ct)).GetProperty("publishedVersion").GetString());

        // Another brain browses: the listing is there, not installed yet, and its icon serves.
        using var listed = await alice.Http.GetAsync(market, ct);
        var entry = Assert.Single((await Json(listed, ct)).EnumerateArray().ToArray());
        Assert.Equal("intochat", entry.GetProperty("listing").GetProperty("publisher").GetString());
        Assert.False(entry.GetProperty("installed").GetBoolean());

        using var icon = await alice.Http.GetAsync($"{market}/intochat/echo/icon", ct);
        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<svg id=\"echo\"/>", await icon.Content.ReadAsStringAsync(ct));

        using var missing = await alice.Http.GetAsync($"{market}/intochat/nothing", ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Install lands in alice's apps with its origin; a second install clashes.
        using var installed = await alice.Http.PostAsync($"{market}/intochat/echo/install", NoRequirements(), ct);
        Assert.Equal("intochat", (await Json(installed, ct)).GetProperty("origin").GetProperty("publisher").GetString());
        using var installedIcon = await alice.Http.GetAsync($"{aliceApps}/echo/icon", ct);
        Assert.Equal(HttpStatusCode.OK, installedIcon.StatusCode);
        Assert.Equal("image/svg+xml", installedIcon.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<svg id=\"echo\"/>", await installedIcon.Content.ReadAsStringAsync(ct));
        using var noApp = await alice.Http.GetAsync($"{aliceApps}/nothing/icon", ct);
        Assert.Equal(HttpStatusCode.NotFound, noApp.StatusCode);
        using var apps = await alice.Http.GetAsync(aliceApps, ct);
        var item = Assert.Single((await Json(apps, ct)).EnumerateArray().ToArray());
        Assert.Equal("icon.svg", item.GetProperty("icon").GetString());
        Assert.Equal("intochat", item.GetProperty("origin").GetProperty("publisher").GetString());
        Assert.Equal(released, item.GetProperty("origin").GetProperty("version").GetString());
        Assert.False(item.GetProperty("updateAvailable").GetBoolean());
        using var again = await alice.Http.PostAsync($"{market}/intochat/echo/install", NoRequirements(), ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The publisher ships a new version; alice sees the update and takes it.
        using (intochat.AsCaller()) { await source.WriteBehavior("synapses/echo.cs", "// v2", Guid.NewGuid()); }
        using var republished = await intochat.Http.PostAsync($"/brains/{intochat.BrainId}/apps/echo/publish", null, ct);
        var next = (await Json(republished, ct)).GetProperty("publishedVersion").GetString();
        using var detail = await alice.Http.GetAsync($"{market}/intochat/echo", ct);
        Assert.True((await Json(detail, ct)).GetProperty("updateAvailable").GetBoolean());
        using var updated = await alice.Http.PostAsync($"{aliceApps}/echo/update", null, ct);
        var update = await Json(updated, ct);
        Assert.Equal(next, update.GetProperty("version").GetString());
        Assert.False(update.GetProperty("edited").GetBoolean());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ADeletedAppCanBeReinstalledFromTheMarketplaceAndIsAttachedToItsOrigin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(ct);
        var intochat = await brain.PersonAsync("intochat", ct);
        var alice = await brain.PersonAsync("alice", ct);
        var source = brain.Get<IApp>(IApp.Key(intochat.BrainId, "echo"));
        using (intochat.AsCaller())
        {
            await source.Create(new("Echo", "echoes", [new("ask", "ask it")], [], []), Spec);
            await source.BeginBuild(null);
            await source.CompleteBuild(await source.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
        }
        using var published = await intochat.Http.PostAsync($"/brains/{intochat.BrainId}/apps/echo/publish", null, ct);
        Assert.True(published.IsSuccessStatusCode);
        var aliceApps = $"/brains/{alice.BrainId}/apps";
        var install = $"/brains/{alice.BrainId}/marketplace/intochat/echo/install";
        using var first = await alice.Http.PostAsync(install, NoRequirements(), ct);
        Assert.True(first.IsSuccessStatusCode);

        using var deleted = await alice.Http.DeleteAsync($"{aliceApps}/echo", ct);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var gone = await alice.Http.GetAsync($"{aliceApps}/echo", ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var again = await alice.Http.PostAsync(install, NoRequirements(), ct);

        var reinstalled = await Json(again, ct);
        Assert.Equal("intochat", reinstalled.GetProperty("origin").GetProperty("publisher").GetString());
        Assert.False(reinstalled.GetProperty("edited").GetBoolean());
    }

    // The consent of an app that asks for nothing, as the install panel sends it.
    private static System.Net.Http.Json.JsonContent NoRequirements() => System.Net.Http.Json.JsonContent.Create(new { consent = AppConsent.Of(null) });

    private static async Task<JsonElement> Json(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}
