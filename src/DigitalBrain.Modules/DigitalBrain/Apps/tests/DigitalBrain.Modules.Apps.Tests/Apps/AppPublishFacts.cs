using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Testing.Module;

namespace DigitalBrain.Apps.Tests.Apps;

// Publish lists one green version in the Marketplace; the listing belongs to the app that
// published it, and the published bundle stays that version while the publisher keeps editing.
public sealed class AppPublishFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.\n\n## Scenario: Quiet\nEmpty stays empty.";

    private static AppManifest Manifest() =>
        new("Echo", "echoes", [new("ask", "ask it")], [new("Text", "what to say", "hi")], [], Icon: "icon.svg");

    private static Task<ModuleBrain> Start() =>
        ModuleTest.Create().WithModule<AppsModule>().StartAsync(TestContext.Current.CancellationToken);

    private static async Task<IApp> GreenApp(ModuleBrain brain, string publisher, string name = "echo")
    {
        Caller.Clear();
        var app = brain.Get<IApp>(IApp.Key(Owner.Scope(publisher), name));
        await app.Create(Manifest(), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(),
            new() { ["tests.cs"] = "// t", ["behaviors/echo.cs"] = "// b", ["icon.svg"] = "<svg/>" }, null);
        await app.RecordVerdicts([new("Echoes", true, "ok"), new("Quiet", true, "ok")], green: true);
        return app;
    }

    private static IMarketplace Marketplace(ModuleBrain brain) => brain.Get<IMarketplace>(IMarketplace.Key);

    [Fact]
    public async Task PublishingARedVersionIsRefused()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        await app.WriteFile("notes.md", "after the green run", Guid.NewGuid());
        Owner.Stamp("intochat");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
        Assert.Contains("green", error.Message, StringComparison.Ordinal);
        Caller.Clear();
        Assert.Empty(await Marketplace(brain).List());
    }

    [Fact]
    public async Task PublishingListsTheGreenVersionWithItsScenariosInSpecOrderAndItsIcon()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        var published = await app.Publish();
        Caller.Clear();

        Assert.Equal(published.Version, published.PublishedVersion);
        // Records holding arrays compare the array by reference, so the facts compare fields.
        var listing = Assert.Single(await Marketplace(brain).List());
        Assert.Equal(("intochat", "account-intochat", "intochat", "echo", "Echo", "echoes", published.Version, "icon.svg"),
            (listing.Publisher, listing.PublisherAccountId, listing.PublisherBrainId, listing.Name, listing.Title,
             listing.Description, listing.Version, listing.Icon));
        Assert.Equal(["Echoes", "Quiet"], listing.Scenarios);
        Assert.Equal(published.Version, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Fact]
    public async Task ThePublishedBundleStaysTheReleasedVersionWhileThePublisherKeepsEditing()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        var released = (await app.Publish()).Version;
        Caller.Clear();
        await app.WriteFile("notes.md", "work in progress", Guid.NewGuid());

        var bundle = await app.ExportPublished();
        Assert.Equal(released, bundle.Version);
        Assert.False(bundle.Files.ContainsKey("notes.md"));
        Assert.Equal(released, (await Marketplace(brain).Read("intochat", "echo"))!.Version);
    }

    [Fact]
    public async Task RepublishingAGreenEditReplacesTheListing()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();
        await app.SaveManifest(Manifest() with { Description = "echoes louder" }, Guid.NewGuid());
        await app.RecordVerdicts([new("Echoes", true, "ok"), new("Quiet", true, "ok")], green: true);
        Owner.Stamp("intochat");
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
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();
        await app.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }

    [Fact]
    public async Task ExportingPublishedBeforeAnyPublishIsRefused()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        await Assert.ThrowsAsync<InvalidOperationException>(app.ExportPublished);
    }

    [Fact]
    public async Task PublishingWithoutASignedInPublisherIsRefused()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        await Assert.ThrowsAsync<InvalidOperationException>(app.Publish);
    }

    [Fact]
    public async Task OnlyTheBrainThatOwnsAnAppCanPublishIt()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("mallory");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(app.Publish);
        Caller.Clear();
        Assert.Empty(await Marketplace(brain).List());
    }

    [Fact]
    public async Task TheMarketplaceRefusesWritesFromAnythingButTheOwningApp()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        await app.Publish();
        Caller.Clear();

        var forged = new MarketplaceListing("intochat", "account-intochat", "intochat", "echo", "Echo", "forged", "9.9.9", null, []);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Upsert(forged));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => Marketplace(brain).Remove("intochat", "echo"));
        Assert.Equal("echoes", (await Marketplace(brain).Read("intochat", "echo"))!.Description);
    }

    [Fact]
    public async Task AnySignedInBrainCanReadTheMarketplace()
    {
        await using var brain = await Start();
        var app = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
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
        var first = await GreenApp(brain, "intochat");
        Owner.Stamp("intochat");
        await first.Publish();
        Caller.Clear();
        var siblingScope = DigitalBrain.Framework.Enforcement.BrainScope.Create("account-intochat", "second").Id;
        var sibling = brain.Get<IApp>(IApp.Key(siblingScope, "echo"));
        await sibling.Create(Manifest(), Spec);
        await sibling.BeginBuild(null);
        await sibling.CompleteBuild(await sibling.CurrentBuildAttempt(), new() { ["tests.cs"] = "// t", ["behaviors/echo.cs"] = "// b" }, null);
        await sibling.RecordVerdicts([new("Echoes", true, "ok"), new("Quiet", true, "ok")], green: true);
        DigitalBrain.Framework.Enforcement.CallerContextStamper.Stamp(new DigitalBrain.Enforcement.CallerContext
        {
            PrincipalId = "intochat",
            AccountId = "account-intochat",
            BrainId = "second",
            Kind = DigitalBrain.Enforcement.CallerKind.Platform,
            StampedBy = DigitalBrain.Enforcement.TrustedEdge.Platform,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(sibling.Publish);
        Caller.Clear();

        Assert.Equal("intochat", (await Marketplace(brain).Read("intochat", "echo"))!.PublisherBrainId);
        await first.Delete();
        Assert.Null(await Marketplace(brain).Read("intochat", "echo"));
    }
}
