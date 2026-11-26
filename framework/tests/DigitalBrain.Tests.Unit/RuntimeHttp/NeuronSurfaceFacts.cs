using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Http.Surface;

namespace DigitalBrain.Runtime.Http.Tests.Unit;

public sealed class NeuronSurfaceFacts
{
    [NeuronAlias("test.surface-probe"), DefaultNeuronType("test.surface-probe")]
    public interface ISurfaceProbe
    {
        Task<string> Echo(string text, int count = 1, CancellationToken ct = default);
    }

    private sealed class CapturingDispatcher : IRuntimeInvocationDispatcher
    {
        public string? LastBrainId { get; private set; }
        public RuntimeInvocation? LastInvocation { get; private set; }

        public Task<JsonElement?> DispatchAsync(string brainId, RuntimeInvocation invocation, CancellationToken ct)
        {
            LastBrainId = brainId;
            LastInvocation = invocation;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("ok"));
        }
    }

    [Fact]
    public async Task Call_binds_named_object_and_positional_array_arguments()
    {
        var dispatcher = new CapturingDispatcher();
        var surface = new NeuronSurface(dispatcher);
        var contract = typeof(ISurfaceProbe).FullName!;
        var ct = TestContext.Current.CancellationToken;

        var named = await surface.Call("b1", contract, "b1/probe/1", "Echo", """{"text":"hi"}""", ct);
        Assert.Equal("\"ok\"", named!.Value.GetRawText());
        Assert.Equal("b1", dispatcher.LastBrainId);
        Assert.Equal(["\"hi\"", "1"], dispatcher.LastInvocation!.Arguments.Select(static a => a.GetRawText()));

        _ = await surface.Call("b1", contract, "b1/probe/1", "Echo", """["hello",3]""", ct);
        Assert.Equal(["\"hello\"", "3"], dispatcher.LastInvocation!.Arguments.Select(static a => a.GetRawText()));
    }

    [Fact]
    public async Task Call_maps_binding_and_json_errors_to_argument_exception()
    {
        var surface = new NeuronSurface(new CapturingDispatcher());
        var contract = typeof(ISurfaceProbe).FullName!;
        var ct = TestContext.Current.CancellationToken;

        var missing = await Assert.ThrowsAsync<ArgumentException>(() => surface.Call("b1", contract, "b1/probe/1", "Echo", "{}", ct));
        Assert.Contains("missing 'text'", missing.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call("b1", contract, "b1/probe/1", "Echo", "{bad json", ct));
    }
}
