using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class MarketplaceAndSharePackageFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DurableMarketplaceNeuronStoresListsReadsAndRemovesPackageListings()
    {
        // The system brain is platform territory: its directory, marketplace and circles are reached by platform code.
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        await using var os = TestOS.Local().RegisterCatalog();
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

    // An App-Post carries its package's unit files in the post store (the profile kept them before), and a remix
    // published from the stored post still has them.
    [Fact]
    public async Task PostStoreShareAndRemixPreserveAppPackageUnitFiles()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "alice", new BrainId("alice")));
        await using var os = TestOS.Local().RegisterCatalog();
        var posts = os.Get<IntoChat.OS.Modules.Apps.Social.IPosts>(new NeuronId(new BrainId("alice"), "social.posts", IntoChat.OS.Modules.Apps.Social.IPosts.DefaultKey));

        var package = ShippedPackages.Find("email-notifier")!;
        var bundle = package.ToAppBundle();
        var files = bundle.Files.OrderBy(static file => file.Key, StringComparer.Ordinal).Select(static file => new IntoChat.OS.Modules.Apps.Social.SharedSourceFile(file.Key, file.Value)).ToArray();
        await posts.Publish(new IntoChat.OS.Modules.Apps.Social.PostDraft(
            "Shareable email notifier automation",
            [],
            new IntoChat.OS.Modules.Apps.Social.AppDefinitionShare(package.Key, "Email Notifier", files),
            Id: "shared-email-notifier",
            Title: "Email Notifier",
            EmbeddedApps: [new("main", package.Key, package.Title, "alice/email-notifier")]), TestContext.Current.CancellationToken);

        var shared = Assert.Single((await posts.Since(0, TestContext.Current.CancellationToken)).Posts);
        await posts.Publish(new IntoChat.OS.Modules.Apps.Social.PostDraft(
            "My Email Notifier",
            [],
            shared.SharedApp,
            Id: "remixed-email-notifier",
            Title: "My Email Notifier",
            ForkedFrom: "@alice/shared-email-notifier"), TestContext.Current.CancellationToken);

        var remixed = Assert.Single((await posts.Since(0, TestContext.Current.CancellationToken)).Posts, p => p.Id == "remixed-email-notifier");
        var paths = remixed.SharedApp!.Files.Select(static file => file.Path).ToArray();
        Assert.Contains(AppPackageExtensions.PackageManifestFile, paths);
        Assert.Contains("EmailNotifierApp.cs", paths);
        Assert.Equal(bundle.Files["EmailNotifierApp.cs"], remixed.SharedApp.Files.Single(static file => file.Path == "EmailNotifierApp.cs").Content);
    }
}
