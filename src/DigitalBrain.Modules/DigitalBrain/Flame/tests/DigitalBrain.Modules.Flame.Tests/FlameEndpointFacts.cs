using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flame.Stage;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests;

// The Open posture's plain client is the brain owner "owner", as the shell is on the owner's machine.
public sealed class FlameEndpointFacts
{
    private const string Scene = "/brains/owner/ui/scenes/owner%2Fapps%2Fino%2Fscenes%2Fhome";

    [Fact]
    public async Task TheShellReadsASceneFiresAnEventAndReportsAGestureOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>("owner/apps/ino/scenes/home");
        await scene.Set(Scenes.Ino(), 0);
        var http = brain.HttpClient;

        using var read = await http.GetAsync(Scene, ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var state = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("idle", state.GetProperty("current").GetProperty("presence").GetString());

        using var fired = await http.PostAsJsonAsync($"{Scene}/fire", new { @event = "think", nodeId = (string?)null }, ct);
        Assert.Equal(HttpStatusCode.OK, fired.StatusCode);
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);

        using var reported = await http.PostAsJsonAsync($"{Scene}/report", new { nodeId = "sphere", kind = "tap", x = 0.5, y = 0.5, payload = (string?)null }, ct);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
    }

    [Fact]
    public async Task TheShellMovesItsOwnStageOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var http = brain.HttpClient;
        const string Stage = "/brains/owner/ui/stage";

        using var home = await http.PostAsJsonAsync($"{Stage}/home/set", new { sceneKey = "owner/apps/ino/scenes/home" }, ct);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        using var go = await http.PostAsJsonAsync($"{Stage}/go", new { sceneKey = "owner/apps/brainview/scenes/brain", dockSurfaceKey = (string?)null }, ct);
        Assert.Equal(HttpStatusCode.OK, go.StatusCode);
        var pushed = await http.GetFromJsonAsync<JsonElement>(Stage, ct);
        Assert.Equal(2, pushed.GetProperty("stack").GetArrayLength());

        using var back = await http.PostAsync($"{Stage}/back", null, ct);
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var popped = await http.GetFromJsonAsync<JsonElement>(Stage, ct);
        Assert.Equal(1, popped.GetProperty("stack").GetArrayLength());
        Assert.Equal("owner/apps/ino/scenes/home", popped.GetProperty("stack")[0].GetProperty("sceneKey").GetString());
        // The owner's shell moved the alias stage, the one its synapses address.
        Assert.Single((await brain.Get<IStage>("owner/stage/owner").Read()).Stack);
    }
}
