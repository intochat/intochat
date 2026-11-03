using System.Reflection;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using IntoChat.Files;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Apps.Post;
using IntoChat.OS.Apps.Workspace;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.CSharp.Programs;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppDefinitionAndRuntimeChallengerFacts
{
    private static readonly Assembly AppsAssembly = typeof(FilmCraftApp).Assembly;
    private static readonly string ExpectedAssemblyVersion = AppsAssembly.GetName().Version?.ToString() ?? "0.0.0";

    [Theory]
    [InlineData(typeof(FilmCraftApp), "filmcraft", "#8B5CF6", "Studio", "FilmCraftApp")]
    [InlineData(typeof(PhotoCraftApp), "photocraft", "#06B6D4", "Studio", "PhotoCraftApp")]
    [InlineData(typeof(CreatorApp), "creator", "#FF5D8F", "Studio", "CreatorApp")]
    [InlineData(typeof(DesktopApp), "desktop", null, null, "DesktopApp")]
    [InlineData(typeof(GalleryApp), "gallery", "#22C55E", "Studio", "GalleryApp")]
    [InlineData(typeof(InoApp), "ino", "#3D7EFF", "Intelligence", "InoApp")]
    [InlineData(typeof(PostApp), "post", "#FB7185", "Reach", "PostApp")]
    [InlineData(typeof(WorkspaceApp), "workspace", "#A855F7", "Social", "WorkspaceApp")]
    [InlineData(typeof(BrainScene), "brain", null, null, "BrainScene")]
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

    [Theory]
    [InlineData("alpha/app/todo", "alpha/app/todo")]
    [InlineData("alpha/apps/todo", "alpha/app/todo")]
    [InlineData("alpha/todo", "alpha/app/todo")]
    [InlineData("workspace_1/app/app_2", "workspace_1/app/app_2")]
    public void AppProgramKeysNormalizesAppKeysDeterministically(string inputKey, string expectedKey)
    {
        Assert.Equal(expectedKey, AppProgramKeys.For(inputKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("no-slash")]
    [InlineData("/starts-with-slash")]
    [InlineData("too/many/nested/slashes/here")]
    public void AppProgramKeysRejectsMalformedAppKeys(string malformedKey)
    {
        Assert.Throws<ArgumentException>(() => AppProgramKeys.For(malformedKey));
    }

    [Fact]
    public async Task CSharpAppRuntimeValidatesInputFailClosed()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var runtime = new CSharpAppRuntime(services);

        // Deploy with malformed appKey throws ArgumentException synchronously
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Deploy("invalid-key", [], [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Deploy("/invalid-key", [], [], CancellationToken.None));

        // Retire with malformed appKey throws ArgumentException synchronously
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Retire("invalid-key", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.Retire("/invalid-key", CancellationToken.None));
    }
}
