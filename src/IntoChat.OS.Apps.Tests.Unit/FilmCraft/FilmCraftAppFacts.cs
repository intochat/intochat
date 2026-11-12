using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.FilmCraft;

public sealed class FilmCraftAppFacts
{
    public static IEnumerable<TheoryDataRow<ScreenOperationCase>> FilmCraftOperations
        => ScreenBaseline.Cases(typeof(FilmCraftApp), os => os.Register(typeof(AppsModule).Assembly));

    [Theory, MemberData(nameof(FilmCraftOperations))]
    public Task FilmCraftStudioBaseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);

    [Fact]
    public async Task FilmCraftScenariosPass()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(FilmCraftApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.filmcraft", "default"));
        Assert.NotNull(await screen.Read());
    }
}
