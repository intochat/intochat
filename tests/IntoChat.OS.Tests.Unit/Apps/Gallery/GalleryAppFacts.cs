using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Gallery;

public sealed class GalleryAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> GalleryOperations
        => ScreenBaseline.Cases(typeof(GalleryApp), os => os.RegisterCatalog());

    [Theory, MemberData(nameof(GalleryOperations))]
    public Task GalleryBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task GalleryScenariosPass()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        os.Register(typeof(GalleryApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.gallery", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task GalleryLibraryNeuronPersistsFilteredCategoryAndSelection()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local();
        os.Register<GalleryLibrary>();
        var library = os.Get<IGalleryLibrary>(new NeuronId(new BrainId("local"), "os.gallery.library", "library"));
        var state = await library.Filter("chat-lfp", "Table");
        Assert.Equal("chat-lfp", state.Category);
        Assert.Equal("table-card", state.SelectedId);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void GalleryRenderDoesNotEmbedProceduralPngOrFakePostgresSignals()
    {
        var view = GalleryRendering.Render(new GalleryAppState());
        var viewJson = System.Text.Json.JsonSerializer.Serialize(view);
        Assert.DoesNotContain("data:image/png;base64,", viewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("neuron://local/postgres/", viewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("ino.connect-db", viewJson, StringComparison.Ordinal);
        Assert.Contains("os://window/open?app=uikit", viewJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GalleryFilterAndSelectUpdateSelectedCatalogId()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local().RegisterCatalog();
        var gallery = await os.Install<GalleryApp>();

        await gallery.Request("filter", new GalleryFilter("chat-lfp", "Table"));
        Assert.Equal("table-card", gallery.App.State.SelectedId);

        await gallery.Request("select", new GallerySelect("brain-scene-3d"));
        Assert.Equal("brain-scene-3d", gallery.App.State.SelectedId);
    }
}
