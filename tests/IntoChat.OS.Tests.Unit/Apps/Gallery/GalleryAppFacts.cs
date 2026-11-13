using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Gallery;

public sealed class GalleryAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> GalleryOperations
        => ScreenBaseline.Cases(typeof(GalleryApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(GalleryOperations))]
    public Task GalleryBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task GalleryScenariosPass()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(GalleryApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.gallery", "default"));
        Assert.NotNull(await screen.Read());
    }

    [Fact]
    public async Task GalleryLibraryNeuronPersistsFilteredCategoryAndSelection()
    {
        await using var os = TestOS.Local();
        os.Register<GalleryLibrary>();
        var library = os.Get<IGalleryLibrary>(new NeuronId(new BrainId("local"), "os.gallery.library", "library"));
        var state = await library.Filter("chat-lfp", "Table");
        Assert.Equal("chat-lfp", state.Category);
        Assert.Equal("table-card", state.SelectedId);
        Assert.Equal(2, state.Count);
    }
}
