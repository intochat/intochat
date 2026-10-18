using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Apps;

// Publish lists one version in the Marketplace; the listing belongs to the app that published it,
// and the published bundle stays that version while the publisher keeps editing.
public sealed class AppPublishFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.\n\n## Scenario: Quiet\nEmpty stays empty.";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [], Icon: "icon.svg");

    private static Task<ITestBrain> Start(IScriptSandbox? sandbox = null)
    {
        var builder = TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new());
        // The compile gate is the sandbox's verdict; a refusing fake stands in for the C# compiler.
        if (sandbox is not null) { builder.ConfigureServices(services => services.AddSingleton(sandbox)); }
        return builder.StartAsync(Ct);
    }

    private static async Task<IApp> LiveApp(ITestBrain brain, Person publisher, string name = "echo", Dictionary<string, string>? files = null)
    {
        var app = brain.Get<IApp>(IApp.Key(publisher.BrainId, name));
        using (publisher.AsCaller())
        {
            await app.Create(Manifest(), Spec);
            await app.BeginBuild(null);
            await app.CompleteBuild(await app.CurrentBuildAttempt(), files ?? new() { ["synapses/echo.cs"] = "// b", ["icon.svg"] = "<svg/>" }, null);
        }
        return app;
    }

    private static async Task<AppSnapshot> Publish(Person publisher, IApp app)
    {
        using (publisher.AsCaller()) { return await app.Publish(); }
    }

    private static IMarketplace Marketplace(ITestBrain brain) => brain.Get<IMarketplace>(IMarketplace.Key);

    [Trait("host", "own")]
    [Fact]
    public async Task SavingAManifestCannotBypassTheProgramCompileGate()
    {
        await using var brain = await Start(new RefusingSandbox(("app.cs", 1, "entry declaration refused")));
        var person = await brain.PersonAsync("entry-owner", Ct);
        var app = await LiveApp(brain, person);
        using var caller = person.AsCaller();
        var before = await app.Read();
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.SaveManifest(
            before.Manifest with { Description = "changed" }, Guid.NewGuid()));
        var after = await app.Read();
        Assert.Equal(before.Manifest.Description, after.Manifest.Description);
        Assert.Equal(before.Version, after.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PublishingListsTheVersionAndItsIcon()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var published = await Publish(intochat, await LiveApp(brain, intochat));

        Assert.Equal(published.Version, published.PublishedVersion);
        // Records holding arrays compare the array by reference, so the facts compare fields.
        var listing = Assert.Single(await Marketplace(brain).List());
        Assert.Equal(("intochat", new AppSource.Brain(intochat.AccountId, intochat.BrainId), "echo", "Echo", "echoes", published.Version, "icon.svg"),
            (listing.Publisher, listing.Source, listing.Name, listing.Title,
             listing.Description, listing.Version, listing.Icon));
        Assert.Equal(published.Version, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ThePublishedBundleStaysTheReleasedVersionWhileThePublisherKeepsEditing()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat);
        var released = (await Publish(intochat, app)).Version;
        using var _ = intochat.AsCaller();
        await app.WriteFile("notes.md", "work in progress", Guid.NewGuid());

        var bundle = await app.ExportPublished();
        Assert.Equal(released, bundle.Version);
        Assert.False(bundle.Files.ContainsKey("notes.md"));
        Assert.Equal(released, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task RepublishingAnEditReplacesTheListing()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat);
        using var _ = intochat.AsCaller();
        await app.Publish();
        await app.SaveManifest(Manifest() with { Description = "echoes louder" }, Guid.NewGuid());
        var republished = await app.Publish();

        var listing = Assert.Single(await Marketplace(brain).List());
        Assert.Equal(republished.Version, listing.Version);
        Assert.Equal("echoes louder", listing.Description);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DeletingAPublishedAppRemovesItsListing()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat);
        using var _ = intochat.AsCaller();
        await app.Publish();
        await app.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ExportingPublishedBeforeAnyPublishIsRefused()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat);
        using var _ = intochat.AsCaller();
        await Assert.ThrowsAsync<InvalidOperationException>(app.ExportPublished);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PublishingWithoutASignedInPublisherIsRefused()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, await brain.PersonAsync("intochat", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task OnlyTheBrainThatOwnsAnAppCanPublishIt()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, await brain.PersonAsync("intochat", Ct));
        var mallory = await brain.PersonAsync("mallory", Ct);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Publish(mallory, app));
        Assert.Empty(await Marketplace(brain).List());
    }

    [Trait("host", "own")]
    [Fact]
    public async Task TheMarketplaceRefusesWritesFromAnythingButTheOwningApp()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        await Publish(intochat, await LiveApp(brain, intochat));

        using var _ = intochat.AsCaller();
        var forged = new MarketplaceListing("intochat", new AppSource.Brain(intochat.AccountId, intochat.BrainId), "echo", "Echo", "forged", "9.9.9", null);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Upsert(forged));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Remove("intochat", "echo"));
        Assert.Equal("echoes", (await Marketplace(brain).Read("intochat", "echo"))!.Description);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnySignedInBrainCanReadTheMarketplace()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        await Publish(intochat, await LiveApp(brain, intochat));
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        Assert.Single(await Marketplace(brain).List());
        Assert.NotNull(await Marketplace(brain).Read("intochat", "echo"));
    }

    // One person, two brains, one app name: the listing stays with the brain that published it
    // first, and that brain can still delete its app.
    [Trait("host", "own")]
    [Fact]
    public async Task ASiblingBrainCannotTakeOverAListingAndTheOwnerCanStillDelete()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var first = await LiveApp(brain, intochat);
        await Publish(intochat, first);
        using var born = await intochat.Http.PostAsync("/identity/brains", null, Ct);
        born.EnsureSuccessStatusCode();
        var second = (await born.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("brainId").GetString()!;
        var sibling = brain.Get<IApp>(IApp.Key(second, "echo"));
        await sibling.Create(Manifest(), Spec);
        await sibling.BeginBuild(null);
        await sibling.CompleteBuild(await sibling.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// b" }, null);

        using var refused = await intochat.Http.PostAsync($"/brains/{second}/apps/echo/publish", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(intochat.BrainId, ((AppSource.Brain)(await Marketplace(brain).Read("intochat", "echo"))!.Source).BrainId);
        using var _ = intochat.AsCaller();
        await first.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PublishingABundleWhoseScriptDoesNotCompileIsRefusedWithTheDiagnostics()
    {
        await using var brain = await Start(new RefusingSandbox(("synapses/echo.cs", 2, "Cannot implicitly convert type 'string' to 'int'")));
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat);
        using var _ = intochat.AsCaller();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
        Assert.Contains("synapses/echo.cs:2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cannot implicitly convert", error.Message, StringComparison.Ordinal);
        Assert.Empty(await Marketplace(brain).List());
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PublishingWithoutASandboxComposedSkipsTheCompileGate()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var published = await Publish(intochat, await LiveApp(brain, intochat));
        Assert.Equal(published.Version, published.PublishedVersion);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task PublishingAnAppWithNoScriptsNeedsNoCompiler()
    {
        await using var brain = await Start(new RefusingSandbox(("any.cs", 1, "would refuse")));
        var intochat = await brain.PersonAsync("intochat", Ct);
        var app = await LiveApp(brain, intochat, "prose", new() { ["notes.md"] = "no code" });
        var published = await Publish(intochat, app);
        Assert.Equal(published.Version, published.PublishedVersion);
    }

    private sealed class RefusingSandbox(params (string File, int Line, string Message)[] errors) : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptCompilationCheck(errors.Length == 0, [.. errors.Select(error => new ScriptCompilationDiagnostic(error.File, "CS0029", error.Line, error.Message))]));
    }
}
