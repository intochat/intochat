using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.PhotoCraft;

public sealed class PhotoCraftAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> PhotoCraftOperations
        => ScreenBaseline.Cases(typeof(PhotoCraftApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(PhotoCraftOperations))]
    public Task PhotoCraftStudioBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task PhotoCraftScenariosPass()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(PhotoCraftApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.photocraft", "default"));
        Assert.NotNull(await screen.Read());
    }
}
