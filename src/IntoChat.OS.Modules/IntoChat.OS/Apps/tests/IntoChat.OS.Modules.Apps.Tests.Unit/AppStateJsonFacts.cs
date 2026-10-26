using System.Text.Json;
using IntoChat.OS.Modules.Apps.Signals;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// 06-08: the plain app neuron stores AppState as JSON with the runtime's wire options. Everything the app reads back must survive
// the round trip: the maps behind their views, history records, retained requests and the last activity.
public sealed class AppStateJsonFacts
{
    [Fact]
    public void AnAppStateRoundTripsThroughTheRuntimeJson()
    {
        var manifest = new AppManifest("Echo", "echoes", [new("open", "opens")], [new("tone", "Tone", "calm")], []);
        var state = new AppState { Manifest = manifest, Spec = "spec", Version = "1.2.3", Status = AppStatus.Live, Origin = new("os", new AppSource.Shipped(), "echo", "1.2.3") };
        state.PutFile("synapses/echo.cs", "// echo");
        state.PutSetting("tone", "loud");
        state.PutConnection("db", "postgres/main");
        state.History = [new AppVersionRecord("1.2.3", DateTimeOffset.UnixEpoch, manifest, "spec", state.Files)];
        var request = new AppRequested("open", "now", Guid.NewGuid());
        state.RequestRetention = [new RetainedRequest(1, request)];
        state.RequestSequence = 1;
        state.LastActivity = new AppActivity("echo", "working", "busy");

        var json = JsonSerializer.Serialize(state, DigitalBrain.Invocation.RuntimeProtocol.Json);
        var read = JsonSerializer.Deserialize<AppState>(json, DigitalBrain.Invocation.RuntimeProtocol.Json)!;

        // The maps' views are written beside their entry arrays (the state export reads them) and read back to the same maps.
        Assert.Equal(state.Files, read.Files);
        Assert.Equal(state.Settings, read.Settings);
        Assert.Equal(state.Connections, read.Connections);
        Assert.Equal(manifest.Title, read.Manifest!.Title);
        Assert.Equal(("1.2.3", AppStatus.Live), (read.Version, read.Status));
        Assert.IsType<AppSource.Shipped>(read.Origin!.Source);
        Assert.Equal(state.Files, Assert.Single(read.History).Files);
        Assert.Equal(request, Assert.Single(read.RequestRetention).Request);
        Assert.Equal(state.LastActivity, read.LastActivity);
    }
}
