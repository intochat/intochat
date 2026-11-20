using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Widgetbook;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Widgetbook;

namespace IntoChat.OS.Apps.Tests.Unit.Widgetbook;

public sealed class WidgetbookAppFacts
{
    [Fact]
    public async Task WidgetbookNeuronManagesStoryThemeAndSourceMode()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(
            IntoChat.OS.Tests.TestEdge.Authority.Person("test", "test", new BrainId("test")));
        await using var os = TestOS.Local();
        os.Register(typeof(UiModule).Assembly);

        var wb = os.Get<IWidgetbook>(new NeuronId(new BrainId("test"), "ui.widgetbook", "default"));
        var state = await wb.NavigateStory("foundations/typography", "light");
        Assert.Equal("foundations/typography", state.ActiveStoryPath);
        Assert.Equal("light", state.ThemeMode);
        Assert.Contains("/widgetbook/index.html", state.CurrentUrl, StringComparison.Ordinal);

        var dev = await wb.SwitchSource(WidgetbookSourceMode.DevHotReload);
        Assert.Equal(WidgetbookSourceMode.DevHotReload, dev.SourceMode);
        Assert.Equal("http://localhost:8090", dev.BaseUrl);
    }

    [Fact]
    public async Task WidgetbookAppRendersFullscreenFramelessBrowser()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);
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
