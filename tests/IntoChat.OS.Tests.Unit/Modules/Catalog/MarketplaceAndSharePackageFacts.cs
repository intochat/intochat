using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class MarketplaceAndSharePackageFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DurableMarketplaceNeuronStoresListsReadsAndRemovesPackageListings()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var marketplace = os.Get<IMarketplace>(new NeuronId(new BrainId("system"), "apps.marketplace", "main"));

        var initial = await marketplace.List();
        Assert.Empty(initial);

        var package = ShippedPackages.Find("email-notifier")!;
        var listing = package.ToMarketplaceListing(new AppSource.Brain("alice", "alice")) with
        {
            Publisher = "alice",
        };

        await marketplace.Upsert(listing);

        var listed = await marketplace.List();
        var stored = Assert.Single(listed);
        Assert.Equal("alice", stored.Publisher);
        Assert.Equal("email-notifier", stored.Name);
        Assert.NotNull(stored.Package);
        Assert.Equal(package.Digest, stored.Package!.Digest);

        var read = await marketplace.Read("alice", "email-notifier");
        Assert.NotNull(read);
        Assert.Equal("Email Notifier", read!.Title);

        await marketplace.Remove("alice", "email-notifier");
        Assert.Empty(await marketplace.List());
    }

    [Fact]
    public void AppPackageRoundtripsThroughAppBundleAndProfilePostFork()
    {
        var original = ShippedPackages.Find("downloads-invoices")!;
        var bundle = original.ToAppBundle();

        Assert.True(bundle.Files.ContainsKey(AppPackageExtensions.PackageManifestFile));
        Assert.True(bundle.Files.ContainsKey("DownloadsInvoicesApp.cs"));

        var restored = AppPackageExtensions.TryFromFiles(
            bundle.Name,
            bundle.Version,
            bundle.Files,
            bundle.Manifest.Title,
            bundle.Manifest.Description,
            original.Author);

        Assert.NotNull(restored);
        Assert.Equal(original.Key, restored!.Key);
        Assert.Equal(original.Digest, restored.Digest);
        Assert.Equal(original.Needs.Signals, restored.Needs.Signals);
        Assert.Single(restored.Needs.Slots);
    }

    [Fact]
    public async Task ProfileNeuronShareAndForkPreserveAppPackageUnitFiles()
    {
        await using var os = TestOS.Local().Register(typeof(AppsModule).Assembly);
        var profile = os.Get<IProfile>(new NeuronId(new BrainId("alice"), "os.profile", "profile"));

        var package = ShippedPackages.Find("email-notifier")!;
        var bundle = package.ToAppBundle();
        var post = new ProfilePostItem(
            Id: "shared-email-notifier",
            Content: "Shareable email notifier automation",
            CreatedAt: DateTimeOffset.UtcNow,
            Circle: "public",
            Title: "Email Notifier",
            TemplateAppId: package.Key,
            TemplateSource: bundle.Files["EmailNotifierApp.cs"],
            EmbeddedApps: [new("main", package.Key, package.Title, "alice/email-notifier")],
            UnitFiles: bundle.Files,
            AuthorBrainId: "alice");

        _ = await profile.PublishAppPost(post);
        var stateAfterFork = await profile.ForkPost("shared-email-notifier", "remixed-email-notifier", "My Email Notifier");

        var remixed = Assert.Single(stateAfterFork.Posts, p => p.Id == "remixed-email-notifier");
        Assert.NotNull(remixed.UnitFiles);
        Assert.True(remixed.UnitFiles!.ContainsKey(AppPackageExtensions.PackageManifestFile));
        Assert.True(remixed.UnitFiles.ContainsKey("EmailNotifierApp.cs"));
    }
}
