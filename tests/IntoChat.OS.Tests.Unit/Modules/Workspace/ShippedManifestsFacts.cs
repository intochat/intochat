namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The host shows an app the way the app's manifest says: title, icon and side-panel width come from the manifest, never from the app's name.
public sealed class ShippedManifestsFacts
{
    private static AppManifest Manifest(string title, string? icon = null, double? side = null, bool studio = false, AppOperation[]? ops = null)
        => new(title, "d", ops ?? [], [], [], icon, SideWidth: side, Studio: studio);

    private static ShippedManifests Sample() => new(new Dictionary<string, AppManifest>(StringComparer.Ordinal)
    {
        ["sample"] = Manifest("Sample Studio", "palette", 301d, studio: true, ops: [new("edit", "Edit")]),
        ["glyph"] = Manifest("Glyph", "icon.svg"),
        ["photocraft"] = Manifest("PhotoCraft", "image", studio: true, ops: [new("edit", "Edit")]),
        ["filmcraft"] = Manifest("FilmCraft", "film", studio: true, ops: [new("edit", "Edit")]),
        ["data"] = Manifest("Data Studio", "grid", studio: true, ops: [new("query", "Query")]),
        ["markdraw"] = Manifest("Markdraw", "grid", studio: true, ops: []),
        ["creator"] = Manifest("Creator", "grid", studio: false, ops: [new("edit", "Edit")]),
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

    [Theory]
    // Valid PhotoCraft routes
    [InlineData("routed:photocraft.edit", "photocraft")]
    [InlineData("routed:photocraft.canvas", "photocraft")]
    [InlineData("routed:photocraft.removeBackground", "photocraft")]
    [InlineData("routed:photocraft.", "photocraft")]
    // Valid FilmCraft routes
    [InlineData("routed:filmcraft.timeline", "filmcraft")]
    [InlineData("routed:filmcraft.render", "filmcraft")]
    [InlineData("routed:filmcraft.", "filmcraft")]
    // Missing trailing dot (MUST NOT match)
    [InlineData("routed:photocraft", null)]
    [InlineData("routed:filmcraft", null)]
    // Prefix collision / spoofing attempts (MUST NOT match)
    [InlineData("routed:photocrafting.edit", null)]
    [InlineData("routed:filmcrafty.cut", null)]
    // Case sensitivity
    [InlineData("routed:PHOTOCRAFT.edit", null)]
    [InlineData("ROUTED:photocraft.edit", null)]
    // Studios that declare no edit operation are never forwarded
    [InlineData("routed:markdraw.", null)]
    [InlineData("routed:data.query", null)]
    [InlineData("routed:creator.edit", null)]
    // Malformed / injection
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("routed:", null)]
    [InlineData("routed:photocraft/../../bin", null)]
    [InlineData("routed:photocraft.'; DROP TABLE apps; --", "photocraft")]
    public void InoTurnRouteHints_BoundaryAndAdversarialInputs_ResolvesExpectedStudioApp(
        string routeHint,
        string? expectedStudioApp)
    {
        var studioApp = Sample().StudioFor(routeHint);
        Assert.Equal(expectedStudioApp, studioApp);
    }
}
