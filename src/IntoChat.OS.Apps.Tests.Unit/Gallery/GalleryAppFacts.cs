using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Gallery;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Gallery;

public sealed class GalleryAppFacts
{
    public static IEnumerable<TheoryDataRow<AppOperationCase>> GalleryOperations
        => AppBaseline.Cases(typeof(GalleryApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(GalleryOperations))]
    public Task GalleryBaseline(AppOperationCase operation) => AppBaseline.RunAsync(operation);

    [Fact]
    public async Task GalleryScenariosPass()
    {
        var report = await BddRunner.VerifyAsync<GalleryApp>(
            fakes: os => os.Register(typeof(AppsModule).Assembly),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(report.Passed);
        Assert.Single(report.Scenarios);
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
