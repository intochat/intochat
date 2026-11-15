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

    [Fact]
    public async Task FilmCraftWithoutConnectedMcpFailsHonestlyWithoutProceduralPngOrFakeCli()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<FilmCraftApp>();

        Assert.Null(installed.App.State.WebUrl);
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);

        await installed.Request("export", new FilmCraftExport("/work/out.mp4", "mp4"));
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);
        Assert.Contains("not connected", installed.App.State.Status, StringComparison.OrdinalIgnoreCase);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(installed.App.Render(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("data:image/png;base64,", renderedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("filmcraft-cli", renderedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("18083", renderedJson, StringComparison.Ordinal);
    }
}
