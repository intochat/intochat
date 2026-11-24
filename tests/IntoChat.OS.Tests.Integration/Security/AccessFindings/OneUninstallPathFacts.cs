using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security.AccessFindings;

// One path installs and removes an app: AppInstaller. DELETE /apps/{name}, POST /apps/import and IAppStore.Remove all
// go through its checks: the stamped caller is a person, a root app stays, and what a bundle asks for is consented to.
[Trait("Category", "AccessFindings")]
public sealed class OneUninstallPathFacts(OneUninstallPathFacts.Kernel kernel) : IClassFixture<OneUninstallPathFacts.Kernel>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host => kernel.Host;
    private EdgeAuthority Edge => Host.Services.GetRequiredService<EdgeAuthority>();
    private AppInstaller Installer => Host.Services.GetRequiredService<AppInstaller>();
    private BrainId Brain => new(Host.Scope);
    private Actor.Person Owner => new(AccountSession.DefaultLogin, AccountSession.DefaultLogin);

    private static AppBundle Bundle(string name, AppRequirements? requires = null)
        => new(name, "1.0.0", new AppManifest("Probe " + name, "A probe.", [], [], [], Requires: requires), "A probe.", []);

    private static string NewName() => "probe-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task APerson_ImportsAndRemovesAnOrdinaryApp_OverHttp()
    {
        var name = NewName();

        using var imported = await Send(HttpMethod.Post, "apps/import", Bundle(name));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.True(await Exists(name));

        using var removed = await Send(HttpMethod.Delete, "apps/" + name);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(await Exists(name));
    }

    [Fact]
    public async Task ABundleThatAsksForSomething_ImportsOnlyWithTheConsentToExactlyThat()
    {
        var name = NewName();
        var requires = new AppRequirements(Connectors: ["github"]);

        using var unasked = await Send(HttpMethod.Post, "apps/import", Bundle(name, requires));
        Assert.Equal(HttpStatusCode.Conflict, unasked.StatusCode);
        var answer = await unasked.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("needsConsent", answer.GetProperty("status").GetString());
        Assert.Equal(AppConsent.Of(requires), answer.GetProperty("consent").GetString());
        Assert.False(await Exists(name));

        using var other = await Send(HttpMethod.Post, "apps/import?consent=" + AppConsent.Of(new AppRequirements(Connectors: ["gmail"])), Bundle(name, requires));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.False(await Exists(name));

        using var consented = await Send(HttpMethod.Post, "apps/import?consent=" + AppConsent.Of(requires), Bundle(name, requires));
        Assert.Equal(HttpStatusCode.OK, consented.StatusCode);
        Assert.True(await Exists(name));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("ino")]
    [InlineData("creator")]
    [InlineData("desktop")]
    public async Task ARootApp_IsNotRemovedOrReplaced_ByAnyRoute(string root)
    {
        using var deleted = await Send(HttpMethod.Delete, "apps/" + root);
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);

        using var replaced = await Send(HttpMethod.Post, "apps/import", Bundle(root));
        Assert.Equal(HttpStatusCode.Forbidden, replaced.StatusCode);

        using (Edge.Enter(Edge.Person(Owner.Principal, Owner.Account, Brain)))
        {
            var store = SiloNeurons.Get<IAppStore>(Host.Services, IAppStore.Key(Host.Scope));
            var outcome = await store.Remove(root);
            Assert.False(outcome.Ok);
            Assert.Contains("stays installed", outcome.Message, StringComparison.Ordinal);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Installer.Remove(root, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Installer.Import(Bundle(root), null, Ct));
        }
    }

    public enum Who { NoCaller, App, Agent, Guest }

    [Theory]
    [InlineData(Who.NoCaller)]
    [InlineData(Who.App)]
    [InlineData(Who.Agent)]
    [InlineData(Who.Guest)]
    public async Task OnlyAPerson_ImportsOrRemoves(Who who)
    {
        var name = NewName();
        using (Edge.Enter(Edge.Person(Owner.Principal, Owner.Account, Brain)))
        {
            Assert.True((await Installer.Import(Bundle(name), null, Ct)).Ok);
        }

        Orleans.Runtime.RequestContext.Clear();
        using (who switch
        {
            Who.App => Edge.Enter(Edge.App("some-app", Owner, Brain)),
            Who.Agent => Edge.Enter(Edge.Agent(Owner, Brain)),
            Who.Guest => Edge.Enter(Edge.Guest(Owner.Principal, Owner.Account, Brain)),
            _ => null,
        })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Installer.Remove(name, Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Installer.Import(Bundle(NewName()), null, Ct));
        }

        Assert.True(await Exists(name));
    }

    [Fact]
    public async Task ASignedInPersonOfAnotherBrain_ImportsAndRemovesNothingHere()
    {
        var name = NewName();
        using var imported = await Send(HttpMethod.Post, "apps/import", Bundle(name));
        imported.EnsureSuccessStatusCode();

        using var stranger = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = Host.BaseAddress };
        var principal = "mallory-" + Guid.NewGuid().ToString("N")[..8];
        using var registered = await stranger.PostAsJsonAsync(
            "/identity/register", new { principalId = principal, password = "correct-horse-battery-staple", displayName = principal }, Ct);
        registered.EnsureSuccessStatusCode();

        using var removed = await stranger.DeleteAsync($"/brains/{Host.Scope}/apps/{name}", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
        using var added = await stranger.PostAsJsonAsync($"/brains/{Host.Scope}/apps/import", Bundle(NewName()), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, added.StatusCode);

        Assert.True(await Exists(name));
    }

    // The listing an app published comes down only with that app: Remove is guarded as Upsert is.
    [Fact]
    public async Task AMarketplaceListing_IsRemovedOnlyByTheAppThatOwnsIt()
    {
        var name = NewName();
        var app = SiloNeurons.Get<IApp>(Host.Services, IntoChat.OS.Host.Primitives.AppRef.Of(Brain, name).Key);
        var marketplace = SiloNeurons.Get<IMarketplace>(Host.Services, IMarketplace.Key);
        var publisher = Owner.Principal;
        using (Edge.Enter(Edge.Person(Owner.Principal, Owner.Account, Brain)))
        {
            Assert.True((await Installer.Import(Bundle(name), null, Ct)).Ok);
            await app.Publish();
        }

        // The catalog module's identity in the marketplace's brain, as AppNeuron.Delete enters it, but not from that app.
        using (Edge.Enter(Edge.Module("apps.catalog", new BrainId("system"))))
        {
            Assert.NotNull(await marketplace.Read(publisher, name));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => marketplace.Remove(publisher, name));
            Assert.NotNull(await marketplace.Read(publisher, name));
            // Nothing is listed under this name: there is nothing to guard, and nothing happens.
            await marketplace.Remove(publisher, "never-listed-" + name);
        }

        using (Edge.Enter(Edge.Person(Owner.Principal, Owner.Account, Brain)))
        {
            await Installer.Remove(name, Ct);
        }

        using (Edge.Enter(Edge.Module("apps.catalog", new BrainId("system"))))
        {
            Assert.Null(await marketplace.Read(publisher, name));
        }
    }

    private async Task<bool> Exists(string name)
    {
        using (Edge.Enter(Edge.Person(Owner.Principal, Owner.Account, Brain)))
        {
            var app = SiloNeurons.Get<IApp>(Host.Services, IntoChat.OS.Host.Primitives.AppRef.Of(Brain, name).Key);
            return (await app.Read()).Manifest.Title.Length > 0;
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string route, AppBundle? body = null)
    {
        using var request = new HttpRequestMessage(method, $"/brains/{Host.Scope}/{route}");
        request.Headers.Add(PresenceProof.HeaderName, Host.PresenceToken);
        if (body is not null) { request.Content = JsonContent.Create(body); }
        return await Host.Client.SendAsync(request, Ct);
    }

    public sealed class Kernel : IAsyncLifetime
    {
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;

        public async ValueTask InitializeAsync() => Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(CancellationToken.None);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
