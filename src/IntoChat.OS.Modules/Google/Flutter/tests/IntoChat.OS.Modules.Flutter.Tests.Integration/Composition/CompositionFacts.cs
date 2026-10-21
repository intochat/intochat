using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Layout;
using IntoChat.OS.Flutter.Surface;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class CompositionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task SurfacePreservesOrderedChildrenAndRejectsStaleWritesAfterReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var surface = brain.Get<ISurface>("owner/a/apps/files/surface");
        await surface.Set(new("Files", [new("layout", "owner/a/apps/files/root"), new("text", "owner/a/apps/files/status")]), 0);
        await brain.DeactivateAsync(surface, ct);
        var state = await surface.Read();
        Assert.Equal(1, state.Revision);
        Assert.Equal(["layout", "text"], state.Definition.Children.Select(c => c.Kind));
        await Assert.ThrowsAsync<RevisionConflictException>(() => surface.Set(new("Wrong", []), 0));
        Assert.Equal("Files", (await surface.Read()).Definition.Title);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InvalidCompositionDoesNotReplacePersistedLayout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var layout = brain.Get<ILayout>("owner/a/apps/files/root");
        await layout.Set(new("column", [new("collection", "owner/a/apps/files/items")]), 0);
        await Assert.ThrowsAsync<ArgumentException>(() => layout.Set(new("unknown", []), 1));
        await Assert.ThrowsAsync<ArgumentException>(() => layout.Set(new("row", [], double.NaN), 1));
        Assert.Equal(1, (await layout.Read()).Revision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ALayoutEmbedsTableAndChartNeurons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var layout = brain.Get<ILayout>("owner/a/apps/data/views/v1/layout");
        await layout.Set(new("column", [new("table", "owner/a/apps/data/views/v1/table"), new("chart", "owner/a/apps/data/views/v1/chart")]), 0);
        Assert.Equal(["table", "chart"], (await layout.Read()).Definition.Children.Select(c => c.Kind));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ASurfaceAcceptsASceneChild()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var surface = brain.Get<ISurface>("owner/a/apps/ino/surface");
        await surface.Set(new("Ino", [new(UIVocabulary.SceneType, "owner/a/apps/ino/scenes/home")]), 0);
        Assert.Equal([UIVocabulary.SceneType], (await surface.Read()).Definition.Children.Select(c => c.Kind));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task LayoutsAndSurfacesAcceptTheV2Kinds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        string[] kinds = [UIVocabulary.ToggleType, UIVocabulary.ProgressType, UIVocabulary.InfoBarType, UIVocabulary.ImageType, UIVocabulary.ExpanderType];
        UiChildRef[] children = [.. kinds.Select(kind => new UiChildRef(kind, "owner/a/apps/kit/" + kind))];
        var layout = brain.Get<ILayout>("owner/a/apps/kit/layout");
        await layout.Set(new("column", children), 0);
        Assert.Equal(kinds, (await layout.Read()).Definition.Children.Select(c => c.Kind));
        var surface = brain.Get<ISurface>("owner/a/apps/kit/surface");
        await surface.Set(new("Kit", children), 0);
        Assert.Equal(kinds, (await surface.Read()).Definition.Children.Select(c => c.Kind));
    }
}
