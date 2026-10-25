using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Framework.Tests.Integration;

[GenerateSerializer]
public sealed record Number([property: Id(0)] int Value) : Signal;
[GenerateSerializer]
public sealed record Text([property: Id(0)] string Value) : Signal;
[Alias("test.emitter")]
public interface ITestEmitter : INeuron
{
    Task Emit(int value);
    Task EmitText(string value);
    Task Deactivate();
}
[Alias("test.other-emitter")]
public interface IOtherEmitter : INeuron { Task Emit(int value); }
[GrainType("test-emitter")]
public sealed class TestEmitter : Neuron, ITestEmitter
{
    public Task Emit(int value) => PublishAsync(new Number(value));
    public Task EmitText(string value) => PublishAsync(new Text(value));
    public Task Deactivate() { DeactivateOnIdle(); return Task.CompletedTask; }
}
[GrainType("other-emitter")]
public sealed class OtherEmitter : Neuron, IOtherEmitter
{
    public Task Emit(int value) => PublishAsync(new Number(value));
}

[Alias("test.silo-brain-listener")]
public interface ISiloBrainListener : INeuron
{
    Task<bool> HubIsPresent();
    Task Listen(string sourceId);
    Task<int> Last();
    Task<bool> CancelingTheTokenEndsASubscription(string sourceId);
}

[GrainType("silo-brain-listener")]
public sealed class SiloBrainListener : Neuron, ISiloBrainListener
{
    private int _last;
    private ISignalSubscription<Number>? _subscription;

    public Task<bool> HubIsPresent() => Task.FromResult(ServiceProvider.GetService<LocalSignalHub>() is not null);

    public async Task Listen(string sourceId)
    {
        var brain = ServiceProvider.GetRequiredService<IIntoChatOS>();
        var source = brain.Get<ITestEmitter>(sourceId);
        _subscription = await ServiceProvider.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<Number>(source);
        _ = Drain();
    }

    public Task<int> Last() => Task.FromResult(_last);

    public async Task<bool> CancelingTheTokenEndsASubscription(string sourceId)
    {
        var brain = ServiceProvider.GetRequiredService<IIntoChatOS>();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = await ServiceProvider.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<Number>(brain.Get<ITestEmitter>(sourceId), cancellation.Token);
        await cancellation.CancelAsync();
        await Task.WhenAny(subscription.Completion, Task.Delay(TimeSpan.FromSeconds(5)));
        return subscription.Completion.IsCanceled;
    }

    private async Task Drain()
    {
        if (_subscription is null) { return; }
        await foreach (var number in _subscription.ReadAllAsync())
        {
            _last = number.Value;
        }
    }
}

// What a watcher in these facts does on a nudge: read the publisher's journal after its own cursor, page by page.
internal static class NudgeReader
{
    public const int PageSize = 100;

    public static INeuron Source(IGrainFactory grains, SignalNudge nudge)
        => grains.GetGrain(GrainId.Parse(nudge.Publisher.Name)).AsReference<INeuron>();

    public static async Task<(Signal[] Signals, long Cursor)> ReadAfter(INeuron source, long cursor)
    {
        var read = new List<Signal>();
        while (true)
        {
            var page = await source.Read(cursor, PageSize);
            read.AddRange(page.Signals);
            if (page.Signals.Length < PageSize) { return ([.. read], Math.Max(cursor, page.Last)); }
            cursor = page.Sequences[^1];
        }
    }
}
