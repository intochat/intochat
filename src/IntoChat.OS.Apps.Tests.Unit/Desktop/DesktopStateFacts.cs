using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

// The Desktop keeps its widgets, greeting and layout in its records neuron: they read back after the neuron is deactivated and
// activated again (TestOS.Restart), not from the app's working state.
// Shares RecordingApp.Requests with DesktopAppFacts, so the two never run at once.
[Collection(nameof(RecordingApp))]
public sealed class DesktopStateFacts
{
    private static readonly NeuronId RecordsId = new(new BrainId("local"), "apps.desktop-records", IDesktopRecords.Key);

    private static TestOS Os()
    {
        var os = TestOS.Local();
        os.Register<DesktopRecordsNeuron>();
        return os;
    }

    [Fact]
    public async Task WidgetsPersistAcrossRestart()
    {
        await using var os = Os();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("widget-open", new DesktopWidgetOpen("Clock"));
        await desktop.Request("widget-open", new DesktopWidgetOpen("weather"));
        await desktop.Request("widget-close", new DesktopWidgetClose("weather"));

        await os.Restart();

        Assert.Equal(["clock"], (await os.Get<IDesktopRecords>(RecordsId).Read()).Widgets);
    }

    [Fact]
    public async Task TheGreetingShowsOnceAcrossRestart()
    {
        RecordingApp.Requests.Clear();
        await using var os = Os();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("start", new DesktopStart());
        await desktop.Request("start", new DesktopStart());

        await os.Restart();
        await desktop.Request("start", new DesktopStart());

        Assert.True((await os.Get<IDesktopRecords>(RecordsId).Read()).Greeted);
        Assert.Equal(("apps/ino", "open", "greet"), Assert.Single(RecordingApp.Requests));
    }

    [Fact]
    public async Task TheLayoutChoicePersistsAcrossRestart()
    {
        await using var os = Os();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("layout", new DesktopLayoutChoice());

        await os.Restart();

        Assert.Equal("spatial-grid", (await os.Get<IDesktopRecords>(RecordsId).Read()).Layout);
    }
}
