using System.Reflection;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using IntoChat.Files;
using IntoChat.OS.Apps.Creator;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Friends;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppDefinitionAndRuntimeChallengerFacts
{
    private static readonly Assembly AppsAssembly = typeof(FilmCraftApp).Assembly;
    private static readonly string ExpectedAssemblyVersion = AppsAssembly.GetName().Version?.ToString() ?? "0.0.0";

    [Theory]
    [InlineData(typeof(FilmCraftApp), "filmcraft", "#8B5CF6", "Studio", "FilmCraftApp")]
    [InlineData(typeof(PhotoCraftApp), "photocraft", "#06B6D4", "Studio", "PhotoCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.ArtCraftApp), "artcraft", "#6366F1", "Studio", "ArtCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.VectorCraftApp), "vectorcraft", "#F59E0B", "Studio", "VectorCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.LightCraftApp), "lightcraft", "#38BDF8", "Studio", "LightCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.EffectCraftApp), "effectcraft", "#A855F7", "Studio", "EffectCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.DesignCraftApp), "designcraft", "#EC4899", "Studio", "DesignCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.PdfCraftApp), "pdfcraft", "#EF4444", "Workspace", "PdfCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.WordCraftApp), "wordcraft", "#2563EB", "Workspace", "WordCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.SoundCraftApp), "soundcraft", "#10B981", "Studio", "SoundCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.DeckCraftApp), "deckcraft", "#F97316", "Workspace", "DeckCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.CadCraftApp), "cadcraft", "#0EA5E9", "Studio", "CadCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.GridCraftApp), "gridcraft", "#16A34A", "Workspace", "GridCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.FigCraftApp), "figcraft", "#F43F5E", "Studio", "FigCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.TextureCraftApp), "texturecraft", "#D97706", "Studio", "TextureCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.MaterialCraftApp), "materialcraft", "#EA580C", "Studio", "MaterialCraftApp")]
    [InlineData(typeof(IntoChat.OS.Apps.ArtCraft.SolidCraftApp), "solidcraft", "#14B8A6", "Studio", "SolidCraftApp")]
    [InlineData(typeof(CreatorApp), "creator", "#FF5D8F", "Studio", "CreatorApp")]
    [InlineData(typeof(DesktopApp), "desktop", null, null, "DesktopApp")]
    [InlineData(typeof(GalleryApp), "gallery", "#22C55E", "Studio", "GalleryApp")]
    [InlineData(typeof(InoApp), "ino", "#3D7EFF", "Intelligence", "InoApp")]
    [InlineData(typeof(FriendsApp), "friends", "#FB7185", "Social", "FriendsApp")]
    [InlineData(typeof(WorkspaceApp), "workspace", "#A855F7", "Social", "WorkspaceApp")]
    [InlineData(typeof(FilesApp), "files", "#F6B73C", "Workspace", "FilesApp")]
    public void ShellAppDefinitionMetadataMatchesContract(
        Type appType,
        string expectedId,
        string? expectedAccent,
        string? expectedCategory,
        string expectedName)
    {
        var prop = appType.GetProperty("Definition", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(prop);

        var definition = prop.GetValue(null) as AppDefinition;
        Assert.NotNull(definition);

        Assert.Equal(expectedId, definition.Id);
        Assert.Equal(expectedAccent, definition.Accent);
        Assert.Equal(expectedCategory, definition.Category);
        Assert.Equal(expectedName, definition.Name);
        Assert.Equal(ExpectedAssemblyVersion, definition.Version);
        Assert.Equal(appType, definition.AppType);

        // Discovery via constructor directly matches static Definition property
        var direct = new AppDefinition(appType);
        Assert.Equal(definition.Id, direct.Id);
        Assert.Equal(definition.Accent, direct.Accent);
        Assert.Equal(definition.Category, direct.Category);
        Assert.Equal(definition.Name, direct.Name);
        Assert.Equal(definition.Version, direct.Version);
    }

    [Fact]
    public void FilesAppDefinitionDiscoversStorageCapabilityRequirement()
    {
        var definition = FilesApp.Definition;
        Assert.Contains(Capability.Storage.V1, definition.Requires.Capabilities);
    }

    [Fact]
    public void ShellAppOperationDiscoveryResolvesCaseAndDashes()
    {
        var definition = FilmCraftApp.Definition;
        Assert.NotNull(definition.FindOperation("open"));
        Assert.NotNull(definition.FindOperation("OPEN"));
        Assert.NotNull(definition.FindOperation("Open"));
        Assert.NotNull(definition.FindOperation("run-command"));
        Assert.NotNull(definition.FindOperation("RUN-COMMAND"));
        Assert.NotNull(definition.FindOperation("runcommand"));
        Assert.NotNull(definition.FindOperation("switch-mode"));
        Assert.NotNull(definition.FindOperation("switchmode"));

        Assert.Null(definition.FindOperation("non-existent"));
        Assert.Throws<ArgumentException>(() => definition.FindOperation(""));
        Assert.Throws<ArgumentException>(() => definition.FindOperation("   "));
    }

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(App))]
    [InlineData(typeof(List<>))]
    public void AppDefinitionRejectsInvalidShapes(Type invalidType)
    {
        Assert.Throws<InvalidOperationException>(() => new AppDefinition(invalidType));
    }

    [Fact]
    public async Task InProcessAppRuntimeValidatesInputFailClosed()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new InProcessAppRuntime(services);

        // Deploy with malformed appKey throws ArgumentException synchronously
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Deploy("invalid-key", [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Deploy("/invalid-key", [], CancellationToken.None));

        // Retire with malformed appKey throws ArgumentException synchronously
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Retire("invalid-key", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Retire("/invalid-key", CancellationToken.None));
    }
}
