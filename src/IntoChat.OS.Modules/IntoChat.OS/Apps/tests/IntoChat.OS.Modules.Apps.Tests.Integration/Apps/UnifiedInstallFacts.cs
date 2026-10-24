using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Identity;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Apps;

// One install path for the store, an exact release and the Marketplace route: nothing lands or is granted unless the
// consent is the digest of what the release asks for now, every binding names a connection of its slot's kind, and
// an app that reads data is installed by the brain's owner. Updates that ask for more wait for consent too.
public sealed class UnifiedInstallFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";
    internal static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    internal static readonly AppRequirements Needs = new(["person.email", "person.birthDate"], ["mailbox"]);

    internal static Task<ITestBrain> Start() => TestBrain.Create()
        .WithModule<AppsModule>()
        .WithModule<FixtureConnectorsModule>()
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static AppManifest Manifest(AppRequirements? requires) =>
        new("Echo", "echoes", [new("ask", "ask it")], [], requires is null ? [] : [new("inbox", "mailbox")], Requires: requires);

    internal static async Task<IApp> Publish(ITestBrain brain, Person publisher, AppRequirements? requires)
    {
        var app = brain.Get<IApp>(IApp.Key(publisher.BrainId, "echo"));
        using var _ = publisher.AsCaller();
        await app.Create(Manifest(requires), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
        await app.Publish();
        return app;
    }

    internal static async Task Republish(Person publisher, IApp app, AppRequirements? requires)
    {
        using var _ = publisher.AsCaller();
        await app.SaveManifest(Manifest(requires), Guid.NewGuid());
        await app.Publish();
    }

    internal static AppInstaller Installer(ITestBrain brain) => brain.Services.GetRequiredService<AppInstaller>();
    internal static IIdentityClient Identity(ITestBrain brain) => brain.Services.GetRequiredService<IIdentityClient>();
    internal static IApp Copy(ITestBrain brain, Person person, string name = "echo") => brain.Get<IApp>(IApp.Key(person.BrainId, name));
    internal static InstallRequest Latest(string consent, Dictionary<string, string>? bindings = null) => new(new("intochat", "echo"), consent, Guid.Empty, null, bindings);

    internal static async Task<string[]> Granted(ITestBrain brain, Person person, string name = "echo")
        => [.. (await Identity(brain).ListGrants(person.BrainId)).Where(grant => grant.AppId == IApp.Key(person.BrainId, name))
            .Select(grant => grant.SemanticTypeId).Order(StringComparer.Ordinal)];

    private static async Task Connect(ITestBrain brain, Person person, string connector, string id)
    {
        using var _ = person.AsCaller();
        await brain.Get<IConnection>(ConnectionKeys.For(person.BrainId, connector, id)).Connect("api-key", "fixture-value-0001");
    }

    [Fact]
    public void TheConsentDigestIsTheSortedRequirementsAndNoneIsEmpty()
    {
        Assert.Equal(AppConsent.Of(null), AppConsent.Of(new()));
        Assert.Equal(AppConsent.Of(new(["b", "a"], ["c"])), AppConsent.Of(new(["a", "b"], ["c"])));
        Assert.NotEqual(AppConsent.Of(new(["a"])), AppConsent.Of(new(Connectors: ["a"])));
        Assert.Matches("^[0-9a-f]{64}$", AppConsent.Of(Needs));
        var added = AppConsent.Added(new(["a"], ["c"]), new(["a", "b"], ["c", "d"], ["x"]));
        Assert.Equal(new AppRequirements(["b"], ["d"], ["x"]), added);
        Assert.True(AppConsent.IsEmpty(AppConsent.Added(Needs, Needs)));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallWithCurrentConsentGrantsEveryTypeAndRecordsIt()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        var outcome = await Installer(brain).Install(Latest(AppConsent.Of(Needs)), Ct);

        Assert.Equal((InstallStatus.Ok, "echo"), (outcome.Status, outcome.InstalledName));
        Assert.Equal(["person.birthDate", "person.email"], await Granted(brain, alice));
        Assert.Equal(Needs, (await Copy(brain, alice).Read()).Origin!.Consented);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InstallWithStaleConsentInstallsNothing()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        // The panel showed an earlier release that asked only for the email address.
        var stale = await Installer(brain).Install(Latest(AppConsent.Of(new(["person.email"], ["mailbox"]))), Ct);
        var missing = await Installer(brain).Install(Latest(""), Ct);

        foreach (var outcome in (InstallOutcome[])[stale, missing])
        {
            Assert.Equal(InstallStatus.NeedsConsent, outcome.Status);
            Assert.False(outcome.Ok);
            Assert.Equal(Needs, outcome.Requires);
        }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Empty(await Granted(brain, alice));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ExactReleaseAndLatestShareOnePath()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var source = await Publish(brain, publisher, Needs);
        var release = Assert.Single(await source.PublishedReleases()).Reference;
        var reader = await brain.PersonAsync("reader", Ct);
        var operation = Guid.NewGuid();
        var exact = new InstallRequest(new(release.Publisher, release.Name, release.Version, release.Digest), AppConsent.Of(Needs), operation, "mine");

        using var _ = reader.AsCaller();
        // A fork without the consent of what the release asks for is refused and creates nothing.
        Assert.Equal(InstallStatus.NeedsConsent, (await Installer(brain).Install(exact with { Consent = AppConsent.Of(null) }, Ct, release.Source)).Status);
        Assert.Equal("", (await Copy(brain, reader, "mine").Read()).Manifest.Title);

        Assert.Equal(InstallStatus.Ok, (await Installer(brain).Install(exact, Ct, release.Source)).Status);
        var forked = await Copy(brain, reader, "mine").Read();
        Assert.Equal(release.Digest, forked.Origin!.PublicationDigest);
        Assert.Equal(Needs, forked.Origin.Consented);
        Assert.Equal(new AppInstallReceipt(operation, release, "mine", reader.AccountId), forked.Installation);
        Assert.Equal(["person.birthDate", "person.email"], await Granted(brain, reader, "mine"));

        Assert.Equal(InstallStatus.Ok, (await Installer(brain).Install(Latest(AppConsent.Of(Needs)), Ct)).Status);
        var latest = await Copy(brain, reader).Read();
        Assert.Equal(Needs, latest.Origin!.Consented);
        Assert.Null(latest.Installation);
        Assert.Equal(["person.birthDate", "person.email"], await Granted(brain, reader));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BindingsMustMatchTheSlotsConnector()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "drive", "files-1");
        await Connect(brain, alice, "mailbox", "mail-1");
        await Connect(brain, alice, "mailbox", "mail-gone");
        using (alice.AsCaller()) { await brain.Get<IConnection>(ConnectionKeys.For(alice.BrainId, "mailbox", "mail-gone")).Disconnect(); }

        using var _ = alice.AsCaller();
        var wrongKind = await Installer(brain).Install(Latest(AppConsent.Of(Needs), new() { ["inbox"] = "files-1" }), Ct);
        var unknownSlot = await Installer(brain).Install(Latest(AppConsent.Of(Needs), new() { ["outbox"] = "mail-1" }), Ct);
        var deleted = await Installer(brain).Install(Latest(AppConsent.Of(Needs), new() { ["inbox"] = "mail-gone" }), Ct);

        Assert.All([wrongKind, unknownSlot, deleted], outcome => Assert.Equal(InstallStatus.Refused, outcome.Status));
        Assert.Equal("Choose a mailbox connection for inbox.", wrongKind.Message);
        Assert.Equal("Choose a mailbox connection for inbox.", deleted.Message);
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Empty(await Granted(brain, alice));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task BindingsAreAppliedOnInstall()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "mailbox", "mail-1");

        using var _ = alice.AsCaller();
        var outcome = await Installer(brain).Install(Latest(AppConsent.Of(Needs), new() { ["inbox"] = "mail-1" }), Ct);

        Assert.Equal(InstallStatus.Ok, outcome.Status);
        Assert.Equal("mail-1", (await Copy(brain, alice).Read()).Connections["inbox"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task NonOwnersCannotInstallDataApps()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        using var member = await brain.PersonAsync("member", Ct);
        // Shared into alice's brain as a member, the way the directory shares a brain: under the owner's account.
        await Identity(brain).EnsureMember(new Member
        {
            PrincipalId = member.PrincipalId,
            AccountId = alice.AccountId,
            BrainId = alice.BrainId,
            Role = MemberRole.Member,
            DisplayName = "Member",
            JoinedAt = DateTimeOffset.UnixEpoch,
        });
        using var selected = await member.Http.PostAsJsonAsync("/identity/session/brain", new { alice.AccountId, alice.BrainId }, Ct);
        selected.EnsureSuccessStatusCode();

        using var refused = await member.Http.PostAsJsonAsync($"/brains/{alice.BrainId}/apps/install",
            new { @ref = new { publisher = "intochat", app = "echo" }, consent = AppConsent.Of(Needs) }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(AppInstaller.OwnerOnly, (await Body(refused)).GetProperty("error").GetString());
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Empty(await Granted(brain, alice));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AppCallersGetRefusedForDataApps()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);

        using (alice.AsApp(alice.Key("apps/desktop")))
        {
            var direct = await Installer(brain).Install(Latest(AppConsent.Of(Needs)), Ct);
            Assert.Equal((InstallStatus.Refused, AppInstaller.OwnerOnly), (direct.Status, direct.Message));
            var store = await brain.Get<IAppStore>(IAppStore.Key(alice.BrainId)).Install(Latest(AppConsent.Of(Needs)));
            Assert.Equal(InstallStatus.Refused, store.Status);
        }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Empty(await Granted(brain, alice));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GetRefusesAppsWithRequirements()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), new(Connectors: ["mailbox"]));
        var alice = await brain.PersonAsync("alice", Ct);

        using var _ = alice.AsCaller();
        var outcome = await brain.Get<IAppStore>(IAppStore.Key(alice.BrainId)).Get("intochat", "echo");

        Assert.Equal((InstallStatus.Refused, "Open Apps to review what this app needs."), (outcome.Status, outcome.Message));
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task HttpInstallIsUserOnly()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        var install = $"/brains/{alice.BrainId}/apps/install";

        using var asked = await alice.Http.PostAsJsonAsync(install, new { @ref = new { publisher = "intochat", app = "echo" }, consent = "" }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var requires = (await Body(asked)).GetProperty("requires");
        Assert.Equal(["mailbox"], requires.GetProperty("connectors").EnumerateArray().Select(item => item.GetString()));
        using var installed = await alice.Http.PostAsJsonAsync(install, new { @ref = new { publisher = "intochat", app = "echo" }, consent = AppConsent.Of(Needs) }, Ct);
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        Assert.Equal("echo", (await Body(installed)).GetProperty("installedName").GetString());

        // The route's own check: only a person at the authenticated edge, never an app behind the proxy.
        using (alice.AsCaller()) { Assert.True(AppsEndpoints.IsPerson()); }
        using (alice.AsApp(alice.Key("apps/desktop"))) { Assert.False(AppsEndpoints.IsPerson()); }
        using (alice.AsScheduler()) { Assert.False(AppsEndpoints.IsPerson()); }
    }

    [Trait("host", "own")]
    [Fact]
    public async Task MarketplaceRouteNeedsConsent409()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        var install = $"/brains/{alice.BrainId}/marketplace/intochat/echo/install";

        using var bare = await alice.Http.PostAsync(install, null, Ct);
        using var stale = await alice.Http.PostAsJsonAsync(install, new { consent = AppConsent.Of(null) }, Ct);
        foreach (var refused in (HttpResponseMessage[])[bare, stale])
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var semantic = (await Body(refused)).GetProperty("requires").GetProperty("semanticTypes");
            Assert.Equal(["person.email", "person.birthDate"], semantic.EnumerateArray().Select(item => item.GetString()));
        }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);

        using var consented = await alice.Http.PostAsJsonAsync(install, new { consent = AppConsent.Of(Needs) }, Ct);
        Assert.Equal(HttpStatusCode.OK, consented.StatusCode);
        Assert.Equal("intochat", (await Body(consented)).GetProperty("origin").GetProperty("publisher").GetString());
    }

    // RecordOrigin marks a copy as the publisher's own, unedited: that would defeat every ShippedApps.Is guard,
    // so an app behind the proxy (its own key included) must not reach it; only the kernel and a person may.
    [Trait("host", "own")]
    [Fact]
    public async Task AnAppCannotRecordItsOwnOrigin()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        var own = Copy(brain, alice, "notes");
        using (alice.AsCaller()) { await own.Create(new AppManifest("Notes", "notes", [], [], []), Spec); }
        var forged = new AppOrigin(ShippedApps.Publisher, new AppSource.Shipped(), "notes", "1.0.0");

        using (alice.AsApp(alice.Key("apps/notes")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => own.RecordOrigin(forged)); }
        using (alice.AsApp(alice.Key("apps/desktop")))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => own.RecordOrigin(forged)); }
        Assert.Null((await own.Read()).Origin);

        using (alice.AsCaller()) { await own.RecordOrigin(forged); }
        Assert.Equal(forged.Publisher, (await own.Read()).Origin?.Publisher);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RemoveRefusesShippedRoots()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), null);
        var alice = await brain.PersonAsync("alice", Ct);
        var store = brain.Get<IAppStore>(IAppStore.Key(alice.BrainId));
        using (alice.AsCaller())
        {
            await brain.InstallLatest("intochat", "echo", Ct);
            await Copy(brain, alice, "ino").Import(new("ino", "1.0.0", Manifest(null) with { Title = "Ino" }, Spec, []));
        }

        using (alice.AsCaller())
        {
            foreach (var root in (string[])["settings", "apps", "ino", "desktop", "workspace"])
            { Assert.Equal(InstallStatus.Refused, (await store.Remove(root)).Status); }
        }
        using (alice.AsApp(alice.Key("apps/desktop"))) { Assert.Equal(InstallStatus.Refused, (await store.Remove("echo")).Status); }
        Assert.Equal("Echo", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Equal("Ino", (await Copy(brain, alice, "ino").Read()).Manifest.Title);

        using (alice.AsCaller()) { Assert.Equal(InstallStatus.Ok, (await store.Remove("echo")).Status); }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateThatAddsRequirementsAsksFirst()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var first = new AppRequirements(["person.email"], ["mailbox"]);
        var source = await Publish(brain, publisher, first);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { Assert.Equal(InstallStatus.Ok, (await Installer(brain).Install(Latest(AppConsent.Of(first)), Ct)).Status); }
        var installed = (await Copy(brain, alice).Read()).Version;
        await Republish(publisher, source, Needs);

        using var _ = alice.AsCaller();
        var asked = await Installer(brain).Update(new("echo"), Ct);
        var stale = await Installer(brain).Update(new("echo", AppConsent.Of(first)), Ct);

        foreach (var outcome in (InstallOutcome[])[asked, stale])
        {
            Assert.Equal(InstallStatus.NeedsConsent, outcome.Status);
            Assert.Equal(new AppRequirements(["person.birthDate"], [], []), outcome.Requires);
            Assert.Equal(Needs, outcome.Latest);
        }
        Assert.Equal(installed, (await Copy(brain, alice).Read()).Version);
        Assert.Equal(["person.email"], await Granted(brain, alice));
        var detail = await brain.Get<IAppStore>(IAppStore.Key(alice.BrainId)).Describe(new("intochat", "echo"));
        Assert.Equal(("update", Needs, first), (detail!.Status, detail.Requires, detail.Consented));

        var consented = await Installer(brain).Update(new("echo", AppConsent.Of(Needs)), Ct);
        Assert.Equal(InstallStatus.Ok, consented.Status);
        var updated = await Copy(brain, alice).Read();
        Assert.NotEqual(installed, updated.Version);
        Assert.Equal(Needs, updated.Origin!.Consented);
        Assert.Equal(["person.birthDate", "person.email"], await Granted(brain, alice));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task UpdateWithoutNewRequirementsNeedsNoConsent()
    {
        await using var brain = await Start();
        var publisher = await brain.PersonAsync("intochat", Ct);
        var source = await Publish(brain, publisher, Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await Installer(brain).Install(Latest(AppConsent.Of(Needs)), Ct); }
        var installed = (await Copy(brain, alice).Read()).Version;
        await Republish(publisher, source, new(["person.email"], ["mailbox"]));

        using var _ = alice.AsCaller();
        Assert.Equal(InstallStatus.Ok, (await Installer(brain).Update(new("echo"), Ct)).Status);
        var updated = await Copy(brain, alice).Read();
        Assert.NotEqual(installed, updated.Version);
        // Consent follows the release down: what the person accepted is now what it asks for.
        var narrowed = updated.Origin!.Consented!;
        Assert.Equal((string[])["person.email"], narrowed.SemanticTypes!);
        Assert.Equal((string[])["mailbox"], narrowed.Connectors!);
    }

    // The Open posture's local owner owns its brain exactly as the authority's own owner rule says.
    [Trait("host", "own")]
    [Fact]
    public async Task TheOpenPostureOwnerInstallsDataApps()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var owner = brain.Owner;
        // As in a brain whose authority has recorded no Owner member yet: only the Open posture's rule makes "owner" its owner.
        var unrecorded = UnrecordedMembers.Over(Identity(brain));
        Assert.Null(await unrecorded.Membership(owner.BrainId, owner.PrincipalId));
        var installer = ActivatorUtilities.CreateInstance<AppInstaller>(brain.Services, unrecorded);

        using var _ = owner.AsCaller();
        var outcome = await installer.Install(Latest(AppConsent.Of(Needs)), Ct);

        Assert.Equal(InstallStatus.Ok, outcome.Status);
        Assert.Equal(["person.birthDate", "person.email"], await Granted(brain, owner));
    }

    // The identity authority with no membership recorded for anyone; every other answer is the real one.
    public class UnrecordedMembers : System.Reflection.DispatchProxy
    {
        private IIdentityClient _inner = null!;

        public static IIdentityClient Over(IIdentityClient inner)
        {
            var proxy = Create<IIdentityClient, UnrecordedMembers>();
            ((UnrecordedMembers)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IIdentityClient.Membership)
                ? Task.FromResult<Member?>(null)
                : targetMethod.Invoke(_inner, args);
    }

    // Binding a connection hands the app the brain's account: only the owner binds, and only a live connection.
    [Trait("host", "own")]
    [Fact]
    public async Task BindingsNeedTheOwnerAndAConnectedConnection()
    {
        await using var brain = await Start();
        var mailOnly = new AppRequirements(Connectors: ["mailbox"]);
        await Publish(brain, await brain.PersonAsync("intochat", Ct), mailOnly);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "mailbox", "mail-1");
        using (alice.AsCaller())
        {
            var stale = brain.Get<IConnection>(ConnectionKeys.For(alice.BrainId, "mailbox", "mail-stale"));
            await stale.Connect("api-key", "fixture-value-expired");
            await stale.Probe();
        }

        using (alice.AsApp(alice.Key("apps/desktop")))
        {
            var app = await Installer(brain).Install(Latest(AppConsent.Of(mailOnly), new() { ["inbox"] = "mail-1" }), Ct);
            Assert.Equal((InstallStatus.Refused, AppInstaller.OwnerOnly), (app.Status, app.Message));
        }
        using (alice.AsCaller())
        {
            var stale = await Installer(brain).Install(Latest(AppConsent.Of(mailOnly), new() { ["inbox"] = "mail-stale" }), Ct);
            Assert.Equal((InstallStatus.Refused, "Choose a mailbox connection for inbox."), (stale.Status, stale.Message));
        }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
    }

    // No half-granted state: a binding the app's storage vetoes after the copy landed takes the copy and its grants back.
    [Trait("host", "own")]
    [Fact]
    public async Task AFailedBindingRollsTheInstallBack()
    {
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule>()
            .WithModule<FixtureConnectorsModule>()
            .WithFakeAppRuntime(new())
            .ConfigureServices(services => services.AddSingleton<IAppStorageHook, VetoingHook>())
            .StartAsync(Ct);
        await Publish(brain, await brain.PersonAsync("intochat", Ct), Needs);
        var alice = await brain.PersonAsync("alice", Ct);
        await Connect(brain, alice, "mailbox", "mail-1");

        using var _ = alice.AsCaller();
        await Assert.ThrowsAsync<InvalidOperationException>(()
            => Installer(brain).Install(Latest(AppConsent.Of(Needs), new() { ["inbox"] = "mail-1" }), Ct));

        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
        Assert.Empty(await Granted(brain, alice));
    }

    private sealed class VetoingHook : IAppStorageHook
    {
        public Task OnRetired(string brainId, string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RequireRebindable(string brainId, string appKey, string slot, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The fixture's storage depends on this binding.");
    }

    // The marketplace route and the update route are a person's, as the install route is.
    [Trait("host", "own")]
    [Fact]
    public async Task AppCallersAreRefusedOnTheMarketplaceAndUpdateRoutes()
    {
        await using var brain = await Start();
        await Publish(brain, await brain.PersonAsync("intochat", Ct), null);
        var alice = await brain.PersonAsync("alice", Ct);
        using (alice.AsCaller()) { await brain.InstallLatest("intochat", "echo", Ct); }
        await Copy(brain, alice).Delete();

        using (alice.AsApp(alice.Key("apps/desktop")))
        {
            var market = await MarketplaceEndpoints.Install("intochat", "echo", new(AppConsent.Of(null), null, null), brain, Installer(brain), Ct);
            var update = await AppsEndpoints.Update("echo", new(null), brain, Installer(brain), Ct);
            Assert.Equal(403, Assert.IsAssignableFrom<global::Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(market).StatusCode);
            Assert.Equal(403, Assert.IsAssignableFrom<global::Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(update).StatusCode);
        }
        Assert.Equal("", (await Copy(brain, alice).Read()).Manifest.Title);
    }

    internal static async Task<JsonElement> Body(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
