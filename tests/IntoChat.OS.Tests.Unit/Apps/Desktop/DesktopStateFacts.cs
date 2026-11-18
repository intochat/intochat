using DigitalBrain;
using DigitalBrain.Access;
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

        using (IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/ino", new Actor.Person("p1", "a1"), new BrainId("local"))))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => os.Get<IDesktopRecords>(RecordsId).Write(new DesktopRecords(["weather"], true, "")));
        }

        Assert.Equal(before, (await os.Get<IDesktopRecords>(RecordsId).Read()).Widgets);
        Assert.Equal(journaled, os.Journal(RecordsId.Brain).Signals(RecordsId).Count());
    }

    // One caller kind each: the brain's person, its platform and the owning app write; another app, the agent, the scheduler, the
    // platform of another brain, a caller of another brain and a call with no stamped caller are refused.
    [Theory]
    [InlineData("person", true)]
    [InlineData("desktop", true)]
    [InlineData("other-app", false)]
    [InlineData("agent", false)]
    [InlineData("scheduler", false)]
    [InlineData("platform", true)]
    [InlineData("platform-of-another-brain", false)]
    [InlineData("other-brain", false)]
    [InlineData("none", false)]
    public async Task RecordsWritersByCallerKind(string kind, bool allowed)
    {
        await using var os = Os();
        var records = os.Get<IDesktopRecords>(RecordsId);
        Caller? caller = kind switch
        {
            "person" => IntoChat.OS.Tests.TestEdge.Authority.Person("p1", "a1", new BrainId("local")),
            "desktop" => IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/desktop", new Actor.Person("p1", "a1"), new BrainId("local")),
            "other-app" => IntoChat.OS.Tests.TestEdge.Authority.App("local/apps/ino", new Actor.Person("p1", "a1"), new BrainId("local")),
            "agent" => IntoChat.OS.Tests.TestEdge.Authority.Agent(new Actor.Person("p1", "a1"), new BrainId("local")),
            "scheduler" => IntoChat.OS.Tests.TestEdge.Authority.Scheduler(new BrainId("local")),
            "platform" => IntoChat.OS.Tests.TestEdge.Authority.Kernel(new BrainId("local")),
            "platform-of-another-brain" => IntoChat.OS.Tests.TestEdge.Authority.Kernel(new BrainId("elsewhere")),
            "other-brain" => IntoChat.OS.Tests.TestEdge.Authority.Person("p1", "a1", new BrainId("elsewhere")),
            _ => null,
        };
        Task Write() => records.Write(new DesktopRecords(["weather"], true, ""));
        using (caller is null ? null : IntoChat.OS.Tests.TestEdge.Authority.Enter(caller))
        {
            if (allowed) { await Write(); }
            else { await Assert.ThrowsAsync<UnauthorizedAccessException>(Write); }
        }

        using (Edge.AsPerson()) { Assert.Equal(allowed ? ["weather"] : [], (await records.Read()).Widgets); }
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
