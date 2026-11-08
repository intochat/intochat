using System.Text.Json;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Apps.Tests.Unit.Desktop;

// The Desktop owns its widgets and greeted flag in its own state: both outlive a restart.
// Shares RecordingApp.Requests with DesktopAppFacts, so the two never run at once.
[Collection(nameof(RecordingApp))]
public sealed class DesktopStateFacts
{
    [Fact]
    public async Task WidgetsPersistAcrossRestart()
    {
        await using var os = TestOS.Local();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("widget-open", new DesktopWidgetOpen("Clock"));
        await desktop.Request("widget-open", new DesktopWidgetOpen("weather"));
        await desktop.Request("widget-close", new DesktopWidgetClose("weather"));

        await os.Restart();
        var again = desktop;

        Assert.Equal(["clock"], again.App.State.Widgets);
        // The record the app stores round-trips its widgets (TestOS keeps an installed app's instance across Restart, so the store's
        // JSON is checked as well).
        Assert.Equal(["clock"], Stored(again.App.State).Widgets);
    }

    [Fact]
    public void AStateSavedBeforeTheDesktopOwnedItsRecordsReadsEmpty()
    {
        var old = JsonSerializer.Deserialize<DesktopAppState>(
            """{"LayoutMode":"smart-desktop","IsNarrow":false,"StylePreset":"p","BarLayout":"b","ActiveNeurons":10,"Status":"s","PinnedApps":["ino"]}""")!;
        Assert.Empty(old.Widgets);
        Assert.False(old.Greeted);
    }

    private static DesktopAppState Stored(DesktopAppState state) => JsonSerializer.Deserialize<DesktopAppState>(JsonSerializer.Serialize(state))!;

    [Fact]
    public async Task TheGreetingShowsOnceAcrossRestart()
    {
        RecordingApp.Requests.Clear();
        await using var os = TestOS.Local();
        os.Register<RecordingApp>();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("start", new DesktopStart());
        await desktop.Request("start", new DesktopStart());

        await os.Restart();
        var again = desktop;
        await again.Request("start", new DesktopStart());

        Assert.True(again.App.State.Greeted);
        Assert.True(Stored(again.App.State).Greeted);
        Assert.Equal(("apps/ino", "open", "greet"), Assert.Single(RecordingApp.Requests));
    }
}
