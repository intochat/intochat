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
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(PhotoCraftApp).Assembly);
        var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.photocraft", "default"));
        var initial = await screen.Read();
        Assert.NotNull(initial);
        var initialJson = System.Text.Json.JsonSerializer.Serialize(initial, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("preset-cutout", initialJson, StringComparison.Ordinal);
        Assert.Contains("toggle-layer-0", initialJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhotoCraftWithoutConnectedMcpFailsHonestlyWithoutProceduralPngOrFakeCli()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<PhotoCraftApp>();

        Assert.Null(installed.App.State.WebUrl);
        Assert.Equal(string.Empty, installed.App.State.PreviewDataUrl);
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);

        // 1. Open without MCP backend leaves PreviewDataUrl & LastCliInvocation empty (no procedural PNG / fake CLI)
        await installed.Request("open", new PhotoCraftStudioOpen("/work/portrait.png", "wasm-ui"));
        Assert.Equal(string.Empty, installed.App.State.PreviewDataUrl);
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);
        Assert.Empty(installed.App.State.History);

        // 2. Remove background / apply-preset without connected MCP server fails honestly without inventing edits
        await installed.Request("remove-background", new PhotoCraftRemoveBackground("/work/portrait.png", "wasm-ui"));
        Assert.Equal(string.Empty, installed.App.State.PreviewDataUrl);
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);
        Assert.Empty(installed.App.State.History);
        Assert.Contains("not connected", installed.App.State.Status, StringComparison.OrdinalIgnoreCase);

        await installed.Request("apply-preset", new PhotoCraftApplyPreset("warm"));
        Assert.Equal(string.Empty, installed.App.State.PreviewDataUrl);
        Assert.Equal(string.Empty, installed.App.State.LastCliInvocation);
        Assert.Empty(installed.App.State.History);
        Assert.Contains("not connected", installed.App.State.Status, StringComparison.OrdinalIgnoreCase);

        // 3. Local UI layer visibility toggle & control surface still operate honestly
        await installed.Request("toggle-layer", new PhotoCraftToggleLayer(LayerIndex: 0));
        Assert.Contains(0, installed.App.State.HiddenLayerIndices!);
        Assert.StartsWith("[Hidden] ", installed.App.State.Layers[0], StringComparison.Ordinal);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(installed.App.Render(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("toggle-layer-0", renderedJson, StringComparison.Ordinal);
        Assert.Contains("preset-warm", renderedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image/png;base64,", renderedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("photocraft-cli", renderedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("18082", renderedJson, StringComparison.Ordinal);

        await installed.Request("toggle-layer", new PhotoCraftToggleLayer(LayerIndex: 0));
        Assert.DoesNotContain(0, installed.App.State.HiddenLayerIndices!);
        Assert.False(installed.App.State.Layers[0].StartsWith("[Hidden] ", StringComparison.Ordinal));
    }
}
