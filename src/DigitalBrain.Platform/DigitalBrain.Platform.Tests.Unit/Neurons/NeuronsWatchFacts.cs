using System.Text.Json;
using DigitalBrain.Platform.Neurons.Proto;
using DigitalBrain.Testing.Module;
using DigitalBrain.Testing.Neurons;
using Grpc.Core;

namespace DigitalBrain.Platform.Tests.Unit.Neurons;

public sealed class NeuronsWatchFacts
{
    private static Task<ModuleBrain> Start(CancellationToken ct) => ModuleTest.Create().WithModule<PulseModule>().WithHttpEdge().StartAsync(ct);

    private static CallRequest Call(string key) => new()
    {
        BrainId = "owner", Contract = typeof(IPulse).FullName, Key = key, Method = nameof(IPulse.Pulse),
        Arguments = Google.Protobuf.ByteString.CopyFromUtf8("[1]"),
    };

    // brain_id names the caller's own brain, but the source lives in another one: the grain-level
    // filter refuses both the watch and the call.
    [Fact]
    public async Task AnotherBrainsNeuronIsRefusedOnWatchAndCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var client = NeuronsCallFacts.Client(brain);
        using var call = client.Watch(Watch("someone-else/pulse", nameof(Pulsed)), cancellationToken: ct);
        var watchError = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(StatusCode.PermissionDenied, watchError.StatusCode);
        var callError = await Assert.ThrowsAsync<RpcException>(() => client.CallAsync(Call("someone-else/pulse"), cancellationToken: ct).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, callError.StatusCode);
    }

    // A key without a brain scope is one the filter refuses the retained read on; the watcher must
    // hear that, not sit on a stream that never replays.
    [Fact]
    public async Task AWatchWhoseRetainedReadIsRefusedFailsLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        await brain.Get<IPulse>("pulse-unscoped").Pulse(1);
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch("pulse-unscoped", nameof(Pulsed)), cancellationToken: ct);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
    }

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
