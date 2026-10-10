using System.Text.Json;
using DigitalBrain.Platform.Neurons.Proto;
using DigitalBrain.Testing.Module;
using DigitalBrain.Testing.Module.Neurons;
using Grpc.Core;

namespace DigitalBrain.Platform.Tests.Unit.Neurons;

public sealed class NeuronsWatchFacts
{
    private static Task<ModuleBrain> Start(CancellationToken ct) => ModuleTest.Create().WithModule<PulseModule>().WithHttpEdge().StartAsync(ct);

    private static WatchRequest Watch(string key, params string[] types)
    {
        var request = new WatchRequest { BrainId = "owner" };
        var source = new WatchSource { Contract = typeof(IPulse).FullName, Key = key };
        source.SignalTypes.AddRange(types);
        request.Sources.Add(source);
        return request;
    }

    [Fact]
    public async Task APublishedSignalArrivesAsAnEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch("owner/pulse-live", nameof(Pulsed)), cancellationToken: ct);
        var next = call.ResponseStream.MoveNext(ct);
        await Task.Delay(300, ct); // the subscription registers before the pulse
        await brain.Get<IPulse>("owner/pulse-live").Pulse(7);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(10), ct));
        var envelope = call.ResponseStream.Current;
        Assert.Equal("owner/pulse-live", envelope.Source);
        Assert.Equal(nameof(Pulsed), envelope.Type);
        Assert.Equal(7, JsonDocument.Parse(envelope.Payload.ToStringUtf8()).RootElement.GetProperty("value").GetInt32());
        Assert.True(envelope.ServerTimeMs > 0);
    }

    [Fact]
    public async Task ALateWatcherGetsTheRetainedSignalFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        await brain.Get<IPulse>("owner/pulse-late").Pulse(1);
        await brain.Get<IPulse>("owner/pulse-late").Pulse(2);
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch("owner/pulse-late", nameof(Pulsed)), cancellationToken: ct);
        Assert.True(await call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(2, JsonDocument.Parse(call.ResponseStream.Current.Payload.ToStringUtf8()).RootElement.GetProperty("value").GetInt32());
        Assert.NotEqual(0, call.ResponseStream.Current.Sequence);
    }

    [Fact]
    public async Task AnUnknownSignalTypeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch("owner/pulse-x", "Nope"), cancellationToken: ct);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    // The edge runs the Open posture, where the owner reaches any brain it names; membership refusal
    // is an OS E2E fact. What a watch must still refuse here is a request that names nothing.
    [Fact]
    public async Task AWatchWithNoSourcesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        using var call = NeuronsCallFacts.Client(brain).Watch(new WatchRequest { BrainId = "owner" }, cancellationToken: ct);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task CancellingTheCallEndsTheStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch("owner/pulse-gone", nameof(Pulsed)), cancellationToken: disconnect.Token);
        var next = call.ResponseStream.MoveNext(disconnect.Token);
        await Task.Delay(300, ct);
        await disconnect.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => next.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }
}
