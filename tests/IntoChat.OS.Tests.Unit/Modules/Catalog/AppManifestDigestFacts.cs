using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using IntoChat.OS.Apps.PhotoCraft;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppManifestDigestFacts
{
    private sealed record LegacyAppManifest(
        string Title,
        string Description,
        AppOperation[] Operations,
        AppSetting[] Settings,
        AppSlot[] Slots,
        string? Icon = null,
        AppToolbarEntry[]? Toolbar = null,
        AppRequirements? Requires = null,
        bool Headless = false,
        double? SideWidth = null,
        int? Dock = null,
        bool Studio = false,
        string? Category = null);

    [Fact]
    public void WindowedLaunchIsOmittedFromSerializedJsonAndPreservesDigest()
    {
        var manifest = new AppManifest(
            Title: "Notes",
            Description: "d",
            Operations: [new("add", "Adds")],
            Settings: [],
            Slots: [],
            Icon: "notebook",
            Dock: 2,
            Category: "Workspace",
            Launch: AppLaunch.Windowed);

        var legacy = new LegacyAppManifest(
            Title: "Notes",
            Description: "d",
            Operations: [new("add", "Adds")],
            Settings: [],
            Slots: [],
            Icon: "notebook",
            Dock: 2,
            Category: "Workspace");

        var currentJson = RuntimeProtocol.SerializeToElement(manifest, typeof(AppManifest)).GetRawText();
        var legacyJson = RuntimeProtocol.SerializeToElement(legacy, typeof(LegacyAppManifest)).GetRawText();

        Assert.Equal(legacyJson, currentJson);
        Assert.DoesNotContain("launch", currentJson, StringComparison.OrdinalIgnoreCase);

        var defaultBundle = new AppBundle("notes", "1.0.0", manifest, "spec", new() { ["a.cs"] = "A" });
        var explicitWindowed = defaultBundle with { Manifest = manifest with { Launch = AppLaunch.Windowed } };
        Assert.Equal(AppPublicationDigest.Of(defaultBundle), AppPublicationDigest.Of(explicitWindowed));
    }

    [Fact]
    public void MaximizedLaunchRoundTripsAndChangesDigest()
    {
        var baseManifest = new AppManifest(
            Title: "Canvas",
            Description: "Full viewport canvas.",
            Operations: [new("open", "Open canvas.")],
            Settings: [],
            Slots: [],
            Category: "Studio",
            Launch: AppLaunch.Windowed);

        var maximizedManifest = baseManifest with { Launch = AppLaunch.Maximized };

        var element = RuntimeProtocol.SerializeToElement(maximizedManifest, typeof(AppManifest));
        var roundTripped = RuntimeProtocol.Deserialize(element, typeof(AppManifest)) as AppManifest;

        Assert.NotNull(roundTripped);
        Assert.Equal(AppLaunch.Maximized, roundTripped.Launch);

        var windowedBundle = new AppBundle("canvas", "1.0.0", baseManifest, "spec", new());
        var maximizedBundle = new AppBundle("canvas", "1.0.0", maximizedManifest, "spec", new());
        Assert.NotEqual(AppPublicationDigest.Of(windowedBundle), AppPublicationDigest.Of(maximizedBundle));
    }

    [Fact]
    public void CompiledAppsUsesScreenComponentManifestAsBase()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
            typeof(PhotoCraftApp).Assembly.ManifestModule.ModuleHandle);

        var compiled = CompiledApps.Of(typeof(PhotoCraftApp).Assembly);
        var photo = compiled.Single(static c => c.Bundle.Name == "photocraft");

        var component = ComponentManifests.Of(typeof(PhotoCraftApp));
        Assert.NotNull(component);
        Assert.NotNull(component.Face);
        Assert.Equal("PhotoCraft", photo.Bundle.Manifest.Title);
        Assert.Equal("artcraft:photocraft", photo.Bundle.Manifest.Icon);
        Assert.Equal("Studio", photo.Bundle.Manifest.Category);
        Assert.Equal(5, photo.Bundle.Manifest.Dock);
        Assert.Equal(AppLaunch.Maximized, photo.Bundle.Manifest.Launch);
        Assert.True(photo.Bundle.Manifest.Studio);
        Assert.NotEmpty(photo.Bundle.Manifest.Operations);
    }
}
