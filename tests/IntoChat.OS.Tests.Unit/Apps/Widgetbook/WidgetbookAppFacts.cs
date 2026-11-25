using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Widgetbook;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Widgetbook;

public sealed class WidgetbookAppFacts
{
    [Fact]
    public async Task WidgetbookNeuronManagesStoryThemeAndSourceMode()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(WidgetbookApp).Assembly);

        var wb = os.Get<IWidgetbook>(new NeuronId(new BrainId("test"), "ui.widgetbook", "default"));
        var state = await wb.NavigateStory("foundations/typography", "light");
        Assert.Equal("foundations/typography", state.ActiveStoryPath);
        Assert.Equal("light", state.ThemeMode);
        Assert.Contains("/widgetbook/index.html", state.CurrentUrl, StringComparison.Ordinal);

        var dev = await wb.SwitchSource(WidgetbookSourceMode.DevHotReload);
        Assert.Equal(WidgetbookSourceMode.DevHotReload, dev.SourceMode);
        Assert.Equal("http://localhost:8090", dev.BaseUrl);
    }

    // The class of ui.widgetbook moved out of the control module; a state stored before the move still loads after it.
    [Fact]
    public async Task StoredWidgetbookStateWrittenBeforeTheMoveStillLoads()
    {
        const string stored = """
            {"version":3,"activeStoryPath":"foundations/typography","themeMode":"light","sourceMode":1,"baseUrl":"http://localhost:8090"}
            """;
        var id = new NeuronId(new BrainId("test"), "ui.widgetbook", "default");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(WidgetbookApp).Assembly);
        os.SeedStoredState(id, stored);

        var state = await os.Get<IWidgetbook>(id).Read();

        Assert.Equal(3, state.Version);
        Assert.Equal("foundations/typography", state.ActiveStoryPath);
        Assert.Equal("light", state.ThemeMode);
        Assert.Equal(WidgetbookSourceMode.DevHotReload, state.SourceMode);
        Assert.Equal("http://localhost:8090", state.BaseUrl);
    }

    [Fact]
    public async Task WidgetbookAppRendersFullscreenFramelessBrowser()
    {
        await using var os = TestOS.Local();
        os.RegisterCatalog();
        var installed = await os.Install<WidgetbookApp>();

        var json = System.Text.Json.JsonSerializer.Serialize(
            installed.App.Render(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"chrome\":\"frameless\"", json, StringComparison.Ordinal);
        Assert.Contains("\"edgeToEdge\":true", json, StringComparison.Ordinal);
        Assert.Contains("/widgetbook/index.html", json, StringComparison.Ordinal);

        await installed.Request("switch-source", new WidgetbookSwitchSource("dev"));
        Assert.Equal(WidgetbookSourceMode.DevHotReload, installed.App.State.SourceMode);
        Assert.StartsWith("http://localhost:8090", installed.App.State.CurrentUrl, StringComparison.Ordinal);
    }
}
