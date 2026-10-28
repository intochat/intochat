using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Modules.Time;
using IntoChat.OS.Modules.Time.Timers.Signals;
using DigitalBrain.Runtime.Orleans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

// 06-07b (R4-17): a live feed of a plain publisher replays the retained last signal, says ready, then streams live ones.
[Trait("host", "own")]
public sealed class PlainLiveFeedFacts
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(15);

    private static async IAsyncEnumerable<DigitalBrain.NeuronId> One(DigitalBrain.NeuronId id, Action listed)
    {
        await Task.CompletedTask;
        yield return id;
        listed();
    }

    [Fact]
    public async Task APlainFeedReplaysTheRetainedSignalThenStreamsLive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<TimeModule>().StartAsync(ct);
        var key = brain.Owner.Key("feed-timer");
        var timer = brain.Get<ITimer>(key);
        var journal = brain.Services.GetRequiredService<IBrainJournalWriter>();
        var id = SiloNeurons.IdOf(typeof(ITimer), key);
        await timer.Start(TimeSpan.FromMilliseconds(10));
        using (var waited = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waited.CancelAfter(Within);
            while ((await journal.ReadSince(id.Brain.Value, id.ToString(), 0, 1, waited.Token)).Last < 1) { await Task.Delay(20, waited.Token); }
        }

        var feed = LiveSignalEndpoints.Feed<TimerTick>((_, _, listed, _) => One(id, listed));
        await using var stream = LiveSignalEndpoints.Stream(brain, brain.Owner.BrainId, [feed], NullLogger.Instance, ct, services: brain.Services).GetAsyncEnumerator(ct);
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Within, ct));
        using (var replay = JsonDocument.Parse(stream.Current))
        {
            Assert.Equal("TimerTick", replay.RootElement.GetProperty("type").GetString());
            Assert.True(replay.RootElement.GetProperty("replay").GetBoolean());
            Assert.Equal(key, replay.RootElement.GetProperty("payload").GetProperty("timerId").GetString());
        }
        Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Within, ct));
        Assert.Equal(LiveSignalEndpoints.Ready, stream.Current);

        var next = stream.MoveNextAsync().AsTask();
        await timer.Start(TimeSpan.FromMilliseconds(10));
        Assert.True(await next.WaitAsync(Within, ct));
        using var live = JsonDocument.Parse(stream.Current);
        Assert.Equal(id.ToString(), live.RootElement.GetProperty("publisher").GetString());
        Assert.False(live.RootElement.TryGetProperty("replay", out var flag) && flag.ValueKind == JsonValueKind.True);
    }
}
