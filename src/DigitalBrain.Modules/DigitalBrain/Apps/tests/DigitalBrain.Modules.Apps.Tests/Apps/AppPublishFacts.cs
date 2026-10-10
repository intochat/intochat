using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

// Publish lists one version in the Marketplace; the listing belongs to the app that published it,
// and the published bundle stays that version while the publisher keeps editing.
public sealed class AppPublishFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.\n\n## Scenario: Quiet\nEmpty stays empty.";

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [], Icon: "icon.svg");

    private static Task<ModuleBrain> Start() =>
        ModuleTest.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(TestContext.Current.CancellationToken);

    private static async Task<IApp> LiveApp(ModuleBrain brain, string publisher, string name = "echo")
    {
        Caller.Clear();
        await AppOwner.Own(brain, publisher);
        var app = brain.Get<IApp>(IApp.Key(AppOwner.Scope(publisher), name));
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { ["synapses/echo.cs"] = "// b", ["icon.svg"] = "<svg/>" }, null);
        return app;
    }

    private static IMarketplace Marketplace(ModuleBrain brain) => brain.Get<IMarketplace>(IMarketplace.Key);

    [Fact]
    public async Task PublishingListsTheVersionAndItsIcon()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        var published = await app.Publish();
        Caller.Clear();

        Assert.Equal(published.Version, published.PublishedVersion);
        // Records holding arrays compare the array by reference, so the facts compare fields.
        var listing = Assert.Single(await Marketplace(brain).List());
        Assert.Equal(("intochat", new AppSource.Brain("account-intochat", "intochat"), "echo", "Echo", "echoes", published.Version, "icon.svg"),
            (listing.Publisher, listing.Source, listing.Name, listing.Title,
             listing.Description, listing.Version, listing.Icon));
        Assert.Equal(published.Version, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Fact]
    public async Task ThePublishedBundleStaysTheReleasedVersionWhileThePublisherKeepsEditing()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        var released = (await app.Publish()).Version;
        Caller.Clear();
        await app.WriteFile("notes.md", "work in progress", Guid.NewGuid());

        var bundle = await app.ExportPublished();
        Assert.Equal(released, bundle.Version);
        Assert.False(bundle.Files.ContainsKey("notes.md"));
        Assert.Equal(released, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Fact]
    public async Task RepublishingAnEditReplacesTheListing()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();
        await app.SaveManifest(Manifest() with { Description = "echoes louder" }, Guid.NewGuid());
        AppOwner.Stamp("intochat");
        var republished = await app.Publish();
        Caller.Clear();

        var listing = Assert.Single(await Marketplace(brain).List());
        Assert.Equal(republished.Version, listing.Version);
        Assert.Equal("echoes louder", listing.Description);
    }

    [Fact]
    public async Task DeletingAPublishedAppRemovesItsListing()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();
        await app.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }

    [Fact]
    public async Task ExportingPublishedBeforeAnyPublishIsRefused()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        await Assert.ThrowsAsync<InvalidOperationException>(app.ExportPublished);
    }

    [Fact]
    public async Task PublishingWithoutASignedInPublisherIsRefused()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
    }

    [Fact]
    public async Task OnlyTheBrainThatOwnsAnAppCanPublishIt()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("mallory");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(app.Publish);
        Caller.Clear();
        Assert.Empty(await Marketplace(brain).List());
    }

    [Fact]
    public async Task TheMarketplaceRefusesWritesFromAnythingButTheOwningApp()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();

        var forged = new MarketplaceListing("intochat", new AppSource.Brain("account-intochat", "intochat"), "echo", "Echo", "forged", "9.9.9", null);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Upsert(forged));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Remove("intochat", "echo"));
        Assert.Equal("echoes", (await Marketplace(brain).Read("intochat", "echo"))!.Description);
    }

    [Fact]
    public async Task AnySignedInBrainCanReadTheMarketplace()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        await app.Publish();
        Caller.As("alice");
        Assert.Single(await Marketplace(brain).List());
        Assert.NotNull(await Marketplace(brain).Read("intochat", "echo"));
        Caller.Clear();
    }

    // One person, two brains, one app name: the listing stays with the brain that published it
    // first, and that brain can still delete its app.
    [Fact]
    public async Task ASiblingBrainCannotTakeOverAListingAndTheOwnerCanStillDelete()
    {
        await using var brain = await Start();
        var first = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        await first.Publish();
        Caller.Clear();
        var siblingScope = DigitalBrain.Framework.BrainScope.Create("account-intochat", "second").Id;
        var sibling = brain.Get<IApp>(IApp.Key(siblingScope, "echo"));
        await sibling.Create(Manifest(), Spec);
        await sibling.BeginBuild(null);
        await sibling.CompleteBuild(await sibling.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// b" }, null);
        DigitalBrain.Framework.CallerContextStamper.Stamp(new DigitalBrain.Framework.CallerContext
        {
            PrincipalId = "intochat",
            AccountId = "account-intochat",
            BrainId = "second",
            Kind = DigitalBrain.Framework.CallerKind.Platform,
            StampedBy = DigitalBrain.Framework.TrustedEdge.Platform,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(sibling.Publish);
        Caller.Clear();

        Assert.Equal("intochat", ((AppSource.Brain)(await Marketplace(brain).Read("intochat", "echo"))!.Source).BrainId);
        await first.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }

    [Fact]
    public async Task PublishingABundleWhoseScriptDoesNotCompileIsRefusedWithTheDiagnostics()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .WithFakeAppRuntime(new())
            .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(new RefusingSandbox(("synapses/echo.cs", 2, "Cannot implicitly convert type 'string' to 'int'"))))
            .StartAsync(ct);
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
        Caller.Clear();
        Assert.Contains("synapses/echo.cs:2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Cannot implicitly convert", error.Message, StringComparison.Ordinal);
        Assert.Empty(await Marketplace(brain).List());
        await Assert.ThrowsAsync<InvalidOperationException>(app.Export);
    }

    [Fact]
    public async Task PublishingWithoutASandboxComposedSkipsTheCompileGate()
    {
        await using var brain = await Start();
        var app = await LiveApp(brain, "intochat");
        AppOwner.Stamp("intochat");
        var published = await app.Publish();
        Caller.Clear();
        Assert.Equal(published.Version, published.PublishedVersion);
    }

    [Fact]
    public async Task PublishingAnAppWithNoScriptsNeedsNoCompiler()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>()
            .WithFakeAppRuntime(new())
            .ConfigureSilo(silo => silo.Services.AddSingleton<IScriptSandbox>(new RefusingSandbox(("any.cs", 1, "would refuse"))))
            .StartAsync(ct);
        Caller.Clear();
        await AppOwner.Own(brain, "intochat");
        var app = brain.Get<IApp>(IApp.Key(AppOwner.Scope("intochat"), "prose"));
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["notes.md"] = "no code" }, null);
        AppOwner.Stamp("intochat");
        var published = await app.Publish();
        Caller.Clear();
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
