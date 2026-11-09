namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// What the desktop's tabs and dock and an Ino chat card show for each shipped app, read from its manifest. A label or icon that changes
// here changes on screen, so each is pinned.
public sealed class ShippedPresentationFacts
{
    [Fact]
    public async Task EveryShippedAppShowsItsManifestTitleAndIcon()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly]);
        var manifests = await ShippedManifests.Of(catalog);
        (string App, string Title, string Icon)[] expected =
        [
            ("creator", "Creator", "grid"), ("data", "Data Studio", "grid"), ("desktop", "Desktop", "grid"), ("files", "Files", "folder"),
            ("filmcraft", "FilmCraft", "film"), ("gallery", "UI Gallery", "widgets"), ("ino", "Ino", "grid"), ("markdraw", "Markdraw", "grid"),
            ("photocraft", "PhotoCraft", "image"), ("post", "Post", "grid"), ("settings", "Settings", "settings"), ("tasks", "Tasks", "tasks"),
            ("workspace", "Workspace", "grid"),
        ];
        foreach (var (app, title, icon) in expected)
        {
            Assert.Equal(title, manifests.Title(app));
            Assert.Equal(icon, manifests.Icon(app));
        }
        // An app the catalog does not ship (the circle tab) is titled from its id.
        Assert.Equal("Circle", manifests.Title("circle"));
        Assert.Same(manifests, await ShippedManifests.Of(catalog));
    }

    [Fact]
    public async Task OnlyCreatorAndGalleryWidenTheSidePanel()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly]);
        var widths = (await catalog.Bundles()).ToDictionary(entry => entry.Key.Name, entry => ShippedManifests.SideWidth(entry.Value.Manifest), StringComparer.Ordinal);
        Assert.Equal(340d, widths["creator"]);
        Assert.Equal(268d, widths["gallery"]);
        Assert.All(widths.Where(entry => entry.Key is not ("creator" or "gallery")), entry => Assert.Equal(ShippedManifests.DefaultSideWidth, entry.Value));
    }

    [Fact]
    public async Task TheDockHoldsTheShellAppsInTheirOrderAndFourAppsAreStudios()
    {
        var catalog = new FeatureCatalog([typeof(AppsModule).Assembly, typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly]);
        var manifests = await ShippedManifests.Of(catalog);
        Assert.Equal(["ino", "files", "data", "markdraw", "photocraft", "filmcraft", "creator", "gallery", "friends", "settings"], manifests.DockApps());
        Assert.Equal(["data", "filmcraft", "markdraw", "photocraft"], manifests.Studios().Select(studio => studio.App));
        // A routed prompt goes to a studio that declares edit, and only with the dot after the app.
        Assert.Equal("photocraft", manifests.StudioFor("routed:photocraft.edit"));
        Assert.Equal("filmcraft", manifests.StudioFor("routed:filmcraft."));
        Assert.Null(manifests.StudioFor("routed:photocraft"));
        Assert.Null(manifests.StudioFor("routed:data.query"));
        Assert.Null(manifests.StudioFor("routed:markdraw."));
        Assert.Null(manifests.StudioFor("routed:creator.new"));
    }
}
