namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The host shows an app the way the app's manifest says: title, icon and side-panel width come from the manifest, never from the app's name.
public sealed class ShippedManifestsFacts
{
    private static AppManifest Manifest(string title, string? icon = null, double? side = null)
        => new(title, "d", [], [], [], icon, SideWidth: side);

    private static ShippedManifests Sample() => new(new Dictionary<string, AppManifest>(StringComparer.Ordinal)
    {
        ["sample"] = Manifest("Sample Studio", "palette", 301d),
        ["glyph"] = Manifest("Glyph", "icon.svg"),
    });

    [Fact]
    public void ATitleAndIconComeFromTheManifest()
    {
        var manifests = Sample();
        Assert.Equal("Sample Studio", manifests.Title("sample"));
        Assert.Equal("palette", manifests.Icon("sample"));
        Assert.Equal("Glyph", manifests.Title("glyph"));
        Assert.Equal("grid", manifests.Icon("glyph"));
        Assert.Equal("Unknown", manifests.Title("unknown"));
        Assert.Equal("grid", manifests.Icon("unknown"));
    }

    [Fact]
    public void ASideWidthComesFromTheManifestElseTheShellDefault()
    {
        Assert.Equal(301d, ShippedManifests.SideWidth(Manifest("t", side: 301d)));
        Assert.Equal(ShippedManifests.DefaultSideWidth, ShippedManifests.SideWidth(Manifest("t")));
    }

    [Fact]
    public void TheChatCardOfARoutedAppReadsItsManifest()
    {
        var turn = new InoTurn("open it", "Opened.", "routed:sample.open", 0);
        var entry = AppPresentationSink.BuildAssistantChatEntry(1, turn, new InoAppState(), Sample());
        Assert.Equal("Sample Studio", entry.App!.Title);
        Assert.Equal("palette", entry.App.Icon);
    }
}
