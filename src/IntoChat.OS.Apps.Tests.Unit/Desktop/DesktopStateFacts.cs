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
    public async Task OnlyTheDesktopWritesItsRecords()
    {
        using var person = Edge.AsPerson();
        await using var os = Os();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("widget-open", new DesktopWidgetOpen("clock"));
        var before = (await os.Get<IDesktopRecords>(RecordsId).Read()).Widgets;
        var journaled = os.Journal(RecordsId.Brain).Signals(RecordsId).Count();

        using (DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(new DigitalBrain.Capabilities.CapabilityCaller("p1", "a1", "local", AppId: "local/apps/ino")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IDesktopRecords>(RecordsId).Write(new DesktopRecords(["weather"], true, "")));
        }

        Assert.Equal(before, (await os.Get<IDesktopRecords>(RecordsId).Read()).Widgets);
        Assert.Equal(journaled, os.Journal(RecordsId.Brain).Signals(RecordsId).Count());
    }

    // One caller kind each: the brain's person and the owning app write; another app, the agent, the scheduler, the platform, a
    // caller of another brain and a call with no stamped caller are refused.
    [Theory]
    [InlineData("person", true)]
    [InlineData("desktop", true)]
    [InlineData("other-app", false)]
    [InlineData("agent", false)]
    [InlineData("scheduler", false)]
    [InlineData("platform", false)]
    [InlineData("other-brain", false)]
    [InlineData("none", false)]
    public async Task RecordsWritersByCallerKind(string kind, bool allowed)
    {
        await using var os = Os();
        var records = os.Get<IDesktopRecords>(RecordsId);
        DigitalBrain.Capabilities.CapabilityCaller? caller = kind switch
        {
            "person" => new("p1", "a1", "local"),
            "desktop" => new("p1", "a1", "local", AppId: "local/apps/desktop"),
            "other-app" => new("p1", "a1", "local", AppId: "local/apps/ino"),
#pragma warning disable CS0618
            "agent" => new("p1", "a1", "local", AppId: DigitalBrain.Capabilities.CapabilityCallers.AssistantApp),
#pragma warning restore CS0618
            "scheduler" => new("p1", "a1", "local", AppId: DigitalBrain.Capabilities.CapabilityCallers.SchedulerApp),
            "platform" => DigitalBrain.Capabilities.CapabilityCallers.Platform("p1", "a1", "local"),
            "other-brain" => new("p1", "a1", "elsewhere"),
            _ => null,
        };
        Task Write() => records.Write(new DesktopRecords(["weather"], true, ""));
        using (caller is null ? null : DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(caller))
        {
            if (allowed) { await Write(); }
            else { await Assert.ThrowsAsync<UnauthorizedAccessException>(Write); }
        }

        Assert.Equal(allowed ? ["weather"] : [], (await records.Read()).Widgets);
    }

    [Fact]
    public async Task WidgetsPersistAcrossRestart()
    {
        using var person = Edge.AsPerson();
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
        using var person = Edge.AsPerson();
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
        using var person = Edge.AsPerson();
        await using var os = Os();
        var desktop = await os.Install<DesktopApp>();
        await desktop.Request("layout", new DesktopLayoutChoice());

        await os.Restart();

        Assert.Equal("spatial-grid", (await os.Get<IDesktopRecords>(RecordsId).Read()).Layout);
    }
}
