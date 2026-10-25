using System.Text.Json;
using IntoChat.OS.Authority.Neurons.Proto;
using IntoChat.OS.Modules.Time;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

// 06-07b (R4-17): the shell watches a plain neuron through the same Watch call it uses for grains. The retained last signal
// comes first (read from the brain's log), then live ones through the session synapse neuron.
[Trait("host", "own")]
public sealed class PlainWatchFacts
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    private static WatchRequest Watch(string key)
    {
        var request = new WatchRequest { BrainId = "owner" };
        var source = new WatchSource { Contract = typeof(ITimer).FullName, Key = key };
        source.SignalTypes.Add("TimerTick");
        request.Sources.Add(source);
        return request;
    }

    [Fact]
    public async Task APlainNeuronReplaysItsRetainedLastSignalThenStreamsLiveOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>().StartAsync(ct);
        var key = "owner/watched-timer";
        var timer = brain.Get<ITimer>(key);
        var plain = brain.Services.GetRequiredService<IPlainSignals>();
        var id = brain.Services.GetRequiredService<IPlainNeurons>().IdOf(typeof(ITimer), key);
        // Two ticks before anyone watches: the replay is the second.
        for (var tick = 1; tick <= 2; tick++)
        {
            await timer.Start(TimeSpan.FromMilliseconds(10));
            using var waited = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waited.CancelAfter(Within);
            while ((await plain.Read(id, 0, 1, waited.Token)).Last < tick) { await Task.Delay(20, waited.Token); }
        }
        var retained = await PlainWatch.Retained(plain, id, "time.timer-tick", ct);
        Assert.NotNull(retained);

        using var call = NeuronsCallFacts.Client(brain).Watch(Watch(key), cancellationToken: ct);
        Assert.True(await call.ResponseStream.MoveNext(ct).WaitAsync(Within, ct));
        var replay = call.ResponseStream.Current;
        Assert.Equal(key, replay.Source);
        Assert.Equal("TimerTick", replay.Type);
        Assert.Equal(retained.Sequence, replay.Sequence);
        Assert.Equal(key, JsonDocument.Parse(replay.Payload.ToStringUtf8()).RootElement.GetProperty("timerId").GetString());

        await timer.Start(TimeSpan.FromMilliseconds(10));
        Assert.True(await call.ResponseStream.MoveNext(ct).WaitAsync(Within, ct));
        Assert.Equal("TimerTick", call.ResponseStream.Current.Type);
        Assert.True(call.ResponseStream.Current.Sequence > retained.Sequence);
    }

    [Fact]
    public async Task APlainNeuronWithNothingRetainedStaysQuietUntilItPublishes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>().StartAsync(ct);
        var key = "owner/quiet-timer";
        using var call = NeuronsCallFacts.Client(brain).Watch(Watch(key), cancellationToken: ct);
        var next = call.ResponseStream.MoveNext(ct);
        await Assert.ThrowsAsync<TimeoutException>(() => next.WaitAsync(TimeSpan.FromSeconds(1), ct));
        await brain.Get<ITimer>(key).Start(TimeSpan.FromMilliseconds(10));
        Assert.True(await next.WaitAsync(Within, ct));
        Assert.Equal(key, call.ResponseStream.Current.Source);
    }
}
