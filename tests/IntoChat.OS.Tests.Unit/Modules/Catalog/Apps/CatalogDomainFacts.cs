using DigitalBrain.Brains;
using DigitalBrain.UI;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI.Routing;
using IntoChat.OS.Modules.Flutter;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class CatalogDomainFacts
{
    private static AppBundle CreateBundle(
        string name = "notes",
        string version = "1.0.0",
        string spec = "spec",
        Dictionary<string, string>? files = null,
        AppManifest? manifest = null)
    {
        return new AppBundle(
            name,
            version,
            manifest ?? new AppManifest("Notes App", "A helpful note taking app", [new("add", "Add a note")], [], []),
            spec,
            files ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["main.cs"] = "// main program" });
    }

    [Fact]
    public void PublishedReleaseRejectsNullOrInvalidArguments()
    {
        var bundle = CreateBundle();
        var digest = AppPublicationDigest.Of(bundle);
        var source = new AppSource.Brain("acc1", "b1");
        var reference = new AppPublicationRef("alice", source, "notes", "1.0.0", digest);
        var publication = new AppPublication(reference, DateTimeOffset.UtcNow, bundle.Manifest, "preview");

        Assert.Throws<ArgumentNullException>(() => new PublishedRelease(null!, bundle));
        Assert.Throws<ArgumentNullException>(() => new PublishedRelease(publication, null!));

        // Publication reference with empty publisher
        var invalidPubRef = new AppPublication(reference with { Publisher = "" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(invalidPubRef, bundle));

        // Publication reference with empty name
        var invalidNameRef = new AppPublication(reference with { Name = "" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(invalidNameRef, bundle));

        // Publication reference with empty version
        var invalidVersionRef = new AppPublication(reference with { Version = "" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(invalidVersionRef, bundle));

        // Publication reference with invalid digest
        var invalidDigestRef = new AppPublication(reference with { Digest = "not-a-valid-hex-digest" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(invalidDigestRef, bundle));

        // Version mismatch between bundle and publication reference
        var versionMismatchPub = new AppPublication(reference with { Version = "2.0.0" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(versionMismatchPub, bundle));

        // Name mismatch between bundle and publication reference
        var nameMismatchPub = new AppPublication(reference with { Name = "other" }, DateTimeOffset.UtcNow, bundle.Manifest, "preview");
        Assert.Throws<ArgumentException>(() => new PublishedRelease(nameMismatchPub, bundle));
    }

    [Fact]
    public void PublishedReleaseCreateComputesDigestAndTruncatesPreview()
    {
        var manifest = new AppManifest("Notes App", "Take notes", [new("add", "Add note")], [], []);
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app.cs"] = "public class App {}",
            ["preview.txt"] = new string('x', 20000)
        };
        var versionRecord = new AppVersionRecord("1.0.0", DateTimeOffset.UtcNow, manifest, "notes spec", files);
        var source = new AppSource.Brain("acc-42", "brain-42");

        var release = PublishedRelease.Create(versionRecord, "bob", source, "notes");

        Assert.Equal("bob", release.Publisher);
        Assert.Equal("notes", release.Name);
        Assert.Equal("1.0.0", release.Version);
        Assert.Equal(source, release.Source);
        Assert.Matches("^[0-9a-f]{64}$", release.Digest);

        // Preview should be truncated to MaxPreviewLength (16384)
        Assert.NotNull(release.Preview);
        Assert.Equal(PublishedRelease.MaxPreviewLength, release.Preview.Length);
    }

    [Fact]
    public void PublishedReleaseMatchesAndHasSameContentLogic()
    {
        var bundle = CreateBundle();
        var release = PublishedRelease.FromBundle(bundle, "carol", new AppSource.Brain("acc1", "b1"), DateTimeOffset.UtcNow);

        Assert.True(release.Matches("1.0.0", release.Digest));
        Assert.False(release.Matches("1.0.1", release.Digest));
        Assert.False(release.Matches("1.0.0", new string('0', 64)));

        var duplicate = PublishedRelease.FromBundle(bundle, "carol", new AppSource.Brain("acc1", "b1"), release.PublishedAt);
        Assert.True(release.HasSameContent(duplicate));

        var bundle2 = bundle with { Version = "1.0.1" };
        var release2 = PublishedRelease.FromBundle(bundle2, "carol", new AppSource.Brain("acc1", "b1"), release.PublishedAt);
        Assert.False(release.HasSameContent(release2));
    }

    [Fact]
    public void PublishedReleaseGeneratesExpectedProjections()
    {
        var bundle = CreateBundle();
        var source = new AppSource.Brain("acc1", "b1");
        var release = PublishedRelease.FromBundle(bundle, "david", source, DateTimeOffset.UtcNow);

        // ToRecord
        var record = release.ToRecord();
        Assert.Equal(release.Publication, record.Publication);
        Assert.Equal(release.Bundle, record.Bundle);

        // ToMarketplaceListing
        var listing = release.ToMarketplaceListing();
        Assert.Equal("david", listing.Publisher);
        Assert.Equal(source, listing.Source);
        Assert.Equal("notes", listing.Name);
        Assert.Equal(bundle.Manifest.Title, listing.Title);
        Assert.Equal(bundle.Manifest.Description, listing.Description);
        Assert.Equal("1.0.0", listing.Version);

        // CreateInstallReceipt
        var opId = Guid.NewGuid();
        var receipt = release.CreateInstallReceipt(opId, "target-notes", "installer-acc");
        Assert.Equal(opId, receipt.OperationId);
        Assert.Equal(release.Reference, receipt.Publication);
        Assert.Equal("target-notes", receipt.TargetName);
        Assert.Equal("installer-acc", receipt.AccountId);

        // CreateOrigin
        var origin = release.CreateOrigin("installer-acc");
        Assert.Equal("david", origin.Publisher);
        Assert.Equal(source, origin.Source);
        Assert.Equal("notes", origin.Name);
        Assert.Equal("1.0.0", origin.Version);
        Assert.Equal(release.Digest, origin.PublicationDigest);
        Assert.Equal("installer-acc", origin.InstalledByAccount);
    }

    [Fact]
    public void PublishedReleaseIsReceiptMatchingInvariants()
    {
        var bundle = CreateBundle();
        var source = new AppSource.Brain("acc1", "b1");
        var release = PublishedRelease.FromBundle(bundle, "eve", source, DateTimeOffset.UtcNow);
        var opId = Guid.NewGuid();
        var receipt = release.CreateInstallReceipt(opId, "notes", "user-123");
        var origin = release.CreateOrigin("user-123");

        // Perfect match
        Assert.True(PublishedRelease.IsReceiptMatching(receipt, origin, bundle, "notes", "user-123"));

        // Empty operation ID
        var emptyOpReceipt = receipt with { OperationId = Guid.Empty };
        Assert.False(PublishedRelease.IsReceiptMatching(emptyOpReceipt, origin, bundle, "notes", "user-123"));

        // Target name mismatch
        Assert.False(PublishedRelease.IsReceiptMatching(receipt, origin, bundle, "wrong-name", "user-123"));

        // Account ID mismatch
        Assert.False(PublishedRelease.IsReceiptMatching(receipt, origin, bundle, "notes", "different-user"));

        // Bundle version mismatch
        var wrongVersionBundle = bundle with { Version = "2.0.0" };
        Assert.False(PublishedRelease.IsReceiptMatching(receipt, origin, wrongVersionBundle, "notes", "user-123"));

        // Bundle digest mismatch (modified files)
        var modifiedBundle = bundle with { Spec = "altered content" };
        Assert.False(PublishedRelease.IsReceiptMatching(receipt, origin, modifiedBundle, "notes", "user-123"));

        // Origin mismatch
        var wrongOrigin = origin with { Publisher = "imposter" };
        Assert.False(PublishedRelease.IsReceiptMatching(receipt, wrongOrigin, bundle, "notes", "user-123"));
    }

    [Fact]
    public void CatalogEntryGroupsAccurately()
    {
        var shippedListing = new MarketplaceListing("os", new AppSource.Shipped(), "desktop", "Desktop", "OS Desktop", "1.0.0", null);
        var shippedEntry = CatalogEntry.FromListing(shippedListing);
        Assert.Equal(CatalogGroup.Builtin, shippedEntry.Group("user-1"));
        Assert.Equal("builtin", shippedEntry.GroupName("user-1"));

        var myListing = new MarketplaceListing("me", new AppSource.Brain("user-1", "b1"), "tool", "My Tool", "Desc", "1.0.0", null);
        var myEntry = CatalogEntry.FromListing(myListing);
        Assert.Equal(CatalogGroup.Mine, myEntry.Group("user-1"));
        Assert.Equal("mine", myEntry.GroupName("user-1"));

        var otherListing = new MarketplaceListing("other", new AppSource.Brain("user-2", "b2"), "tool", "Other Tool", "Desc", "1.0.0", null);
        var otherEntry = CatalogEntry.FromListing(otherListing);
        Assert.Equal(CatalogGroup.Others, otherEntry.Group("user-1"));
        Assert.Equal("others", otherEntry.GroupName("user-1"));
    }

    [Fact]
    public void CatalogEntryDisplayTitleAndDescriptionHandlesForks()
    {
        var listing = new MarketplaceListing("upstream", new AppSource.Brain("upstream-acc", "b-upstream"), "widget", "Upstream Widget", "Original description", "1.0.0", "icon.png");

        var localSnapshot = new AppSnapshot(
            Name: "widget",
            Manifest: new AppManifest("Forked Widget", "My customized description", [], [], [], Icon: "fork-icon.png"),
            Spec: "customized spec",
            Files: new Dictionary<string, string>(),
            Settings: new Dictionary<string, string>(),
            Connections: new Dictionary<string, string>(),
            Version: "1.0.0",
            Status: AppStatus.Live,
            Activity: [],
            Versions: [],
            PublishedVersion: null,
            Origin: new AppOrigin("upstream", listing.Source, "widget", "1.0.0"),
            Edited: true,
            Installation: null,
            Presentation: null,
            ProgramRevision: 1);

        var entry = CatalogEntry.FromListing(listing, localSnapshot, "b1");

        Assert.True(entry.IsForked);
        Assert.True(entry.IsInstalled);
        Assert.False(entry.HasUpdate); // Edited copies do not show updates
        Assert.Equal("Copy of Upstream Widget", entry.DisplayTitle);
        Assert.Equal("My customized description", entry.Description);
        Assert.Equal("fork-icon.png", entry.Icon);
        Assert.Equal("mine", entry.GroupName("any-account"));

        var gallery = entry.ToGalleryEntry("any-account");
        Assert.Equal("mine", gallery.Group);
        Assert.Equal("Copy of Upstream Widget", gallery.Title);
        Assert.Equal("My customized description", gallery.Description);
        Assert.Equal("widget", gallery.InstalledName);
    }

    [Fact]
    public void CatalogEntryStatusDeterminationMatrix()
    {
        var reference = new AppRef("publisher", "calc");
        var listing = new MarketplaceListing("publisher", new AppSource.Brain("acc", "b1"), "calc", "Calculator", "Calc app", "2.0.0", null);
        var bundle = CreateBundle("calc", "2.0.0");

        // 1. Available (in store, not installed)
        var availableEntry = CatalogEntry.FromDetail(reference, listing, bundle, local: null);
        Assert.Equal("available", availableEntry.DetermineStatus(releaseAvailable: true));

        // 2. Unavailable (not in store, not installed)
        var unavailEntry = CatalogEntry.FromDetail(reference, null, null, local: null);
        Assert.Equal("unavailable", unavailEntry.DetermineStatus(releaseAvailable: false));

        // 3. Installed (up to date)
        var upToDateLocal = new AppSnapshot(
            Name: "calc",
            Manifest: new AppManifest("Calculator", "Calc", [], [], []),
            Spec: "",
            Files: new Dictionary<string, string>(),
            Settings: new Dictionary<string, string>(),
            Connections: new Dictionary<string, string>(),
            Version: "2.0.0",
            Status: AppStatus.Live,
            Activity: [],
            Versions: [],
            PublishedVersion: null,
            Origin: new AppOrigin("publisher", listing.Source, "calc", "2.0.0"),
            Edited: false,
            Installation: null,
            Presentation: null,
            ProgramRevision: 1);
        var installedEntry = CatalogEntry.FromDetail(reference, listing, bundle, upToDateLocal);
        Assert.Equal("installed", installedEntry.DetermineStatus(releaseAvailable: true));

        // 4. Update available (installed version 1.0.0 < catalog version 2.0.0)
        var olderLocal = upToDateLocal with
        {
            Version = "1.0.0",
            Origin = upToDateLocal.Origin! with { Version = "1.0.0" }
        };
        var updateEntry = CatalogEntry.FromDetail(reference, listing, bundle, olderLocal);
        Assert.True(updateEntry.HasUpdate);
        Assert.Equal("update", updateEntry.DetermineStatus(releaseAvailable: true));
    }

    [Fact]
    public void RuntimeUnitCatalogVisibilityAndRoutability()
    {
        var operations = new[]
        {
            new AppOperation("summarize", "Summarizes input context"),
            new AppOperation("export", "Exports data to json")
        };
        var manifest = new AppManifest("Synthesizer", "AI synthesizer", operations, [], []);
        var presentation = new AppPresentation(
            ProgramRevision: 1,
            Revision: 1,
            Root: new Presentation(
                Sphere: new Sphere("synth.svg", ["running"]),
                Views: [new PresentationView("main", new ViewRef("view", "owner/apps/synth/main"))]),
            Error: null,
            Blackboard: new Dictionary<string, string>());

        var snapshot = new AppSnapshot(
            Name: "synthesizer",
            Manifest: manifest,
            Spec: "synth spec",
            Files: new Dictionary<string, string>(),
            Settings: new Dictionary<string, string>(),
            Connections: new Dictionary<string, string>(),
            Version: "0.1.0",
            Status: AppStatus.Live,
            Activity: [],
            Versions: [],
            PublishedVersion: "0.1.0",
            Origin: null,
            Edited: false,
            Installation: null,
            Presentation: presentation,
            ProgramRevision: 1);

        var entry = CatalogEntry.FromSnapshot("synthesizer", snapshot, "brain-1");

        // Inspectability
        Assert.True(entry.IsRoutable);
        Assert.True(entry.HasUiPresence);
        Assert.Equal("Synthesizer", entry.Title);
        Assert.Equal("0.1.0", entry.Version);
        Assert.Equal(2, entry.Operations.Count);
        Assert.NotNull(entry.Presentation?.Root?.Sphere);

        // Routability: ToMenuOperations maps to MenuOperation records
        var menuOps = entry.ToMenuOperations();
        Assert.Equal(2, menuOps.Count);
        Assert.Contains(menuOps, op => op.App == "synthesizer" && op.Name == "summarize" && op.Description == "Summarizes input context");
        Assert.Contains(menuOps, op => op.App == "synthesizer" && op.Name == "export" && op.Description == "Exports data to json");

        // ResolveOwnerKey
        var listing = entry.ToMarketplaceListing();
        Assert.Equal("brain-1/apps/synthesizer", CatalogEntry.ResolveOwnerKey(listing));
    }

    [Fact]
    public void LegacyAppRecordEncapsulationDetectsAndCleanses()
    {
        var state = new AppState();
        var legacyBehaviorKey = "_" + "_behavior_old";
        state.PutFile("program.cs", "// code");
        state.PutFile("binary.dll.base64", "YWJj");
        state.PutFile("readme.md", "# Hello");
        state.PutSetting(legacyBehaviorKey, "deprecated");
        state.PutSetting("lib.dll.base64", "eHl6");
        state.PutSetting(AppTrust.CompiledTypeSetting, "MyCompiledType");
        state.PutSetting("normalSetting", "value");

        var markers = LegacyAppRecord.DetectMarkers(state);

        Assert.Contains("file:program.cs", markers);
        Assert.Contains("file:binary.dll.base64", markers);
        Assert.Contains($"setting:{legacyBehaviorKey}", markers);
        Assert.Contains("setting:lib.dll.base64", markers);
        Assert.DoesNotContain("file:readme.md", markers);
        Assert.DoesNotContain($"setting:{AppTrust.CompiledTypeSetting}", markers);
        Assert.DoesNotContain("setting:normalSetting", markers);

        LegacyAppRecord.Cleanse(state);

        Assert.False(state.Files.ContainsKey("program.cs"));
        Assert.False(state.Files.ContainsKey("binary.dll.base64"));
        Assert.True(state.Files.ContainsKey("readme.md"));

        Assert.False(state.Settings.ContainsKey(legacyBehaviorKey));
        Assert.False(state.Settings.ContainsKey("lib.dll.base64"));
        Assert.True(state.Settings.ContainsKey(AppTrust.CompiledTypeSetting));
        Assert.True(state.Settings.ContainsKey("normalSetting"));

        // Cleaned state now has 0 legacy markers
        Assert.Empty(LegacyAppRecord.DetectMarkers(state));
    }
}
