namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppPublicationFacts
{
    private static AppBundle Bundle(Dictionary<string, string>? files = null, string spec = "spec")
        => new("notes", "1.0.0", new("Notes", "d", [new("add", "Adds")], [], []), spec, files ?? new() { ["a.cs"] = "A", ["b.cs"] = "B" });

    [Fact]
    public void TheDigestIsSixtyFourLowerCaseHexCharacters()
    {
        var digest = AppPublicationDigest.Of(Bundle());

        Assert.Equal(64, digest.Length);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void TheDigestIsStableAndIgnoresFileInsertionOrder()
    {
        var forward = Bundle(new() { ["a.cs"] = "A", ["b.cs"] = "B" });
        var backward = Bundle(new() { ["b.cs"] = "B", ["a.cs"] = "A" });

        Assert.Equal(AppPublicationDigest.Of(forward), AppPublicationDigest.Of(backward));
        Assert.Equal(AppPublicationDigest.Of(forward), AppPublicationDigest.Of(forward));
    }

    [Fact]
    public void AChangeToAFileTheSpecOrTheManifestChangesTheDigest()
    {
        var baseline = AppPublicationDigest.Of(Bundle());

        Assert.NotEqual(baseline, AppPublicationDigest.Of(Bundle(new() { ["a.cs"] = "A2", ["b.cs"] = "B" })));
        Assert.NotEqual(baseline, AppPublicationDigest.Of(Bundle(spec: "other")));
        Assert.NotEqual(baseline, AppPublicationDigest.Of(Bundle() with { Manifest = Bundle().Manifest with { Title = "Other" } }));
    }

    [Fact]
    public void TheNameAndVersionAreNotPartOfTheDigest()
        => Assert.Equal(AppPublicationDigest.Of(Bundle()), AppPublicationDigest.Of(Bundle() with { Name = "renamed", Version = "9.9.9" }));

    [Fact]
    public void ANullBundleIsRefused()
        => Assert.Throws<ArgumentNullException>(() => AppPublicationDigest.Of(null!));
}
