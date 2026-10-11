using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using Xunit;
namespace DigitalBrain.Modules.Flutter.Tests;

public sealed class ShellPersistenceHttpFacts
{
    [Fact]
    public async Task HttpClientsShareDurableStateAndRejectStaleWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        // Shell state is host-global, and the fact asserts revision zero on an untouched
        // host, so it boots its own in-process brain instead of leasing the shared one.
        await using var brain = await TestBrain.Create()
            .WithModule<DigitalBrain.Flutter.FlutterModule, DigitalBrain.Flutter.FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .StartAsync(ct);
        var http = brain.HttpClient;
        var empty = await http.GetFromJsonAsync<JsonObject>("/shell/state", ct);
        Assert.Equal(0, empty!["revision"]!.GetValue<int>());
        Assert.Null(empty["snapshot"]);
        var snapshot = JsonNode.Parse("""{"version":1,"projects":[{"id":"restored","name":"Saved workspace","conversations":[{"id":"chat","draft":"unfinished","messages":[{"role":"user","content":"remember this"}]}],"artifacts":[]}],"selectedProjectId":"restored","settings":{"theme":"dark"}}""");
        using var first = await http.PutAsJsonAsync("/shell/state", new { expectedRevision = 0, operationId = Guid.NewGuid().ToString(), snapshot }, ct);
        first.EnsureSuccessStatusCode();
        using var fresh = new HttpClient { BaseAddress = http.BaseAddress };
        var restored = await fresh.GetFromJsonAsync<JsonObject>("/shell/state", ct);
        Assert.True(JsonNode.DeepEquals(snapshot, restored!["snapshot"]));
        var savedOperationId = Guid.NewGuid().ToString();
        using var saved = await fresh.PutAsJsonAsync("/shell/state", new { expectedRevision = 1, operationId = savedOperationId, snapshot }, ct);
        saved.EnsureSuccessStatusCode();
        using var stale = await http.PutAsJsonAsync("/shell/state", new { expectedRevision = 1, operationId = Guid.NewGuid().ToString(), snapshot }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var replay = await http.PutAsJsonAsync("/shell/state", new { expectedRevision = 1, operationId = savedOperationId, snapshot }, ct);
        replay.EnsureSuccessStatusCode();
        Assert.Equal(2, (await replay.Content.ReadFromJsonAsync<JsonObject>(ct))!["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheOwnersMachineKeepsTheShellStateInsideTheOwnersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        // The OS silo is the owner's machine: its cluster storage is the part log, which opens only parts of a brain with an owning account.
        await using var brain = await TestBrain.Create()
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Own))
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", "8")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2")
            .WithModule<DigitalBrain.Flutter.FlutterModule, DigitalBrain.Flutter.FlutterModuleOptions>(flutter => flutter.BackendOnly())
            .StartAsync(ct);
        var http = brain.HttpClient;
        using var empty = await http.GetAsync("/shell/state", ct);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var snapshot = JsonNode.Parse("""{"version":1,"projects":[{"id":"restored","name":"Saved workspace","conversations":[],"artifacts":[]}],"selectedProjectId":"restored","settings":{"theme":"dark"}}""");
        using var saved = await http.PutAsJsonAsync("/shell/state", new { expectedRevision = 0, operationId = Guid.NewGuid().ToString(), snapshot }, ct);
        saved.EnsureSuccessStatusCode();
        var restored = await http.GetFromJsonAsync<JsonObject>("/shell/state", ct);
        Assert.Equal(1, restored!["revision"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(snapshot, restored["snapshot"]));
    }
}
