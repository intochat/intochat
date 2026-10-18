using System.Net;
using System.Text.Json;
using DigitalBrain.Client.Runtime.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Microsoft.CSharp.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class RuntimeDepthFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task InvocationAndResponsePreserveDeepStructuredValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await RuntimeBrain.Create(new FakeSessionHost()).StartAsync(ct);
        await RuntimeBrain.Programs(brain, "acc-brain").Assign("acc-brain/csharp/a", RuntimeBrain.One(RuntimeBrain.Idle), []);
        var session = (await RuntimeBrain.Runtime(brain, "acc-brain").Inspect(ct)).SessionId!;
        var token = brain.Services.GetRequiredService<SessionTokens>().Issue("acc-brain", session);
        // 32 inline nodes with a child array at every level, plus the view and invocation envelopes.
        var json = "{\"text\":\"leaf\",\"children\":[]}";
        for (var i = 0; i < 31; i++) { json = "{\"children\":[" + json + "]}"; }
        json = "{\"generation\":1,\"version\":1,\"root\":" + json + "}";
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        var invocation = new RuntimeInvocation("acc-brain/csharp/a", typeof(IDeepWireProbe).FullName!, "acc-brain/probe", nameof(IDeepWireProbe.Echo), [document.RootElement]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/brains/acc-brain/" + RuntimeProtocol.Invoke)
        { Content = JsonContent.Create(invocation, options: RuntimeProtocol.Json) };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await brain.HttpClient.SendAsync(request, ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(RuntimeProtocol.Json, ct);
        var leaf = result.GetProperty("root");
        for (var i = 0; i < 31; i++) { leaf = leaf.GetProperty("children")[0]; }
        Assert.Equal("leaf", leaf.GetProperty("text").GetString());
    }
}

[Alias("tests.csharp.deep-wire"), Public]
public interface IDeepWireProbe : INeuron
{
    Task<DeepWireValue> Echo(DeepWireValue value);
}

public sealed class DeepWireProbe : Neuron, IDeepWireProbe
{
    public Task<DeepWireValue> Echo(DeepWireValue value) => Task.FromResult(value);
}


[GenerateSerializer]
public sealed record DeepWireValue([property: Id(0)] long Generation, [property: Id(1)] long Version, [property: Id(2)] DeepWireNode Root);

[GenerateSerializer]
public sealed record DeepWireNode([property: Id(0)] string? Text, [property: Id(1)] DeepWireNode[] Children);
