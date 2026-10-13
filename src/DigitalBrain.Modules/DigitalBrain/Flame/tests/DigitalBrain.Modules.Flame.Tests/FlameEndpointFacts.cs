using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests;

// The Open posture's plain client is the brain owner "owner", as the shell is on the owner's machine.
public sealed class FlameEndpointFacts
{
    private const string Scene = "/brains/owner/ui/scenes/owner%2Fapps%2Fino%2Fscenes%2Fhome";

    [Fact]
    public async Task TheShellReadsASceneAndReportsAGestureOverHttpAndThereIsNoFireRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>("owner/apps/ino/scenes/home");
        await scene.Set(Scenes.Ino(), 0);
        var http = brain.HttpClient;

        using var read = await http.GetAsync(Scene, ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var state = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("selector", state.GetProperty("definition").GetProperty("nodes")[0].GetProperty("tree").GetProperty("root").GetProperty("kind").GetString());

        using var fired = await http.PostAsJsonAsync($"{Scene}/fire", new { @event = "think", nodeId = (string?)null }, ct);
        Assert.True(fired.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);

        using var reported = await http.PostAsJsonAsync($"{Scene}/report", new { nodeId = "sphere", kind = "tap", x = 0.5, y = 0.5, payload = (string?)null }, ct);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
    }
}
