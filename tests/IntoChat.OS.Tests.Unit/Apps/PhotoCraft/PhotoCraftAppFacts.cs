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
        var initial = await screen.Read();
        Assert.NotNull(initial);
        var initialJson = System.Text.Json.JsonSerializer.Serialize(initial, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("preset-cutout", initialJson, StringComparison.Ordinal);
        Assert.Contains("toggle-layer-0", initialJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PhotoCraftLocalWindowsOperationsUpdatePngPreviewLayersAndHistoryWithoutExternalCli()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
        var installed = await os.Install<PhotoCraftApp>();

        // 1. Open starts with valid PNG data URL even before external CLI runs
        await installed.Request("open", new PhotoCraftStudioOpen("/work/portrait.png", "wasm-ui"));
        AssertValidPngDataUrl(installed.App.State.PreviewDataUrl);
        Assert.Empty(installed.App.State.History);

        // 2. Remove background updates PreviewDataUrl, Layers, and History locally
        await installed.Request("remove-background", new PhotoCraftRemoveBackground("/work/portrait.png", "wasm-ui"));
        AssertValidPngDataUrl(installed.App.State.PreviewDataUrl);
        Assert.Equal("/work/portrait.nobg.png", installed.App.State.OutputDocument);
        Assert.Contains(installed.App.State.Layers, l => l.Contains("Subject Cutout", StringComparison.Ordinal));
        Assert.Single(installed.App.State.History);

        // 3. Apply filter preset ("warm") updates PreviewDataUrl and renders interactive preset/layer controls
        await installed.Request("apply-preset", new PhotoCraftApplyPreset("warm"));
        AssertValidPngDataUrl(installed.App.State.PreviewDataUrl);
        Assert.Equal("warm", installed.App.State.ActivePreset);
        Assert.Equal(2, installed.App.State.History.Length);

        // 4. Toggle layer visibility hides and reveals layer 0 locally
        await installed.Request("toggle-layer", new PhotoCraftToggleLayer(LayerIndex: 0));
        Assert.Contains(0, installed.App.State.HiddenLayerIndices!);
        Assert.StartsWith("[Hidden] ", installed.App.State.Layers[0], StringComparison.Ordinal);
        AssertValidPngDataUrl(installed.App.State.PreviewDataUrl);

        var renderedJson = System.Text.Json.JsonSerializer.Serialize(installed.App.Render(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("toggle-layer-0", renderedJson, StringComparison.Ordinal);
        Assert.Contains("preset-warm", renderedJson, StringComparison.Ordinal);

        await installed.Request("toggle-layer", new PhotoCraftToggleLayer(LayerIndex: 0));
        Assert.DoesNotContain(0, installed.App.State.HiddenLayerIndices!);
        Assert.False(installed.App.State.Layers[0].StartsWith("[Hidden] ", StringComparison.Ordinal));
    }

    private static void AssertValidPngDataUrl(string dataUrl)
    {
        Assert.StartsWith("data:image/png;base64,", dataUrl, StringComparison.Ordinal);
        var bytes = Convert.FromBase64String(dataUrl["data:image/png;base64,".Length..]);
        Assert.True(bytes.Length > 24);
        // PNG magic signature: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], bytes[..8]);
    }
}
