using IntoChat.OS;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Client.Orleans.Tests.Integration;

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
// A source that never publishes itself; EagerJournal writes for it while a watcher is connecting.
[Alias("test.eager-emitter")]
public interface IEagerEmitter : INeuron;
[GrainType("test-eager-emitter")]
public sealed class EagerEmitter : Neuron, IEagerEmitter;

// Journals one signal for the eager emitter right after a watcher's starting read, before it is registered.
// The append raises no nudge, so only the pull that follows registration can deliver it.
public sealed class EagerJournal(global::Microsoft.Extensions.Options.IOptions<MemorySignalJournalOptions> options) : ISignalJournal
{
    private readonly MemorySignalJournal _inner = new(options);
    private int _emitted;
    public Task<long> Append(GrainId publisher, Signal[] signals) => _inner.Append(publisher, signals);
    public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => _inner.ReadSince(brainId, since, limit);
    public async Task<PartSignals?> SignalsOf(GrainId publisher, long since)
    {
        var part = await _inner.SignalsOf(publisher, since);
        if (publisher.Type.ToString() == "test-eager-emitter" && Interlocked.Exchange(ref _emitted, 1) == 0)
        { await _inner.Append(publisher, [new Number(7) { Publisher = publisher.ToString() }]); }
        return part;
    }
}
[Alias("test.flaky-emitter")]
public interface IFlakyEmitter : INeuron
{
    Task FailNextRead();
    Task Emit(int value);
}
[GrainType("test-flaky-emitter")]
public sealed class FlakyEmitter : Neuron, IFlakyEmitter
{
    private bool _fail;
    public Task FailNextRead() { _fail = true; return Task.CompletedTask; }
    public Task Emit(int value) => PublishAsync(new Number(value));
    public override Task<SignalPage> Read(long since, int limit)
    {
        if (_fail && limit > 0) { _fail = false; throw new InvalidOperationException("Injected read failure."); }
        return base.Read(since, limit);
    }
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

[GenerateSerializer]
public sealed record Heard([property: Id(0)] int Value) : Signal;

public interface ISiloBrainListener : INeuron
{
    Task<bool> HubIsPresent();
    Task Listen(string sourceId);
    Task<bool> CancelingTheTokenEndsASubscription(string sourceId);
}

[GrainType("silo-brain-listener")]
public sealed class SiloBrainListener : Neuron, ISiloBrainListener
{
    private ISignalSubscription<Number>? _subscription;

    public Task<bool> HubIsPresent() => Task.FromResult(ServiceProvider.GetService<LocalSignalHub>() is not null);

    public async Task Listen(string sourceId)
    {
        var brain = ServiceProvider.GetRequiredService<IIntoChatOS>();
        var source = brain.Get<ITestEmitter>(sourceId);
        _subscription = await ServiceProvider.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<Number>(source);
        _ = Drain();
    }

    public async Task<bool> CancelingTheTokenEndsASubscription(string sourceId)
    {
        var brain = ServiceProvider.GetRequiredService<IIntoChatOS>();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = await ServiceProvider.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<Number>(brain.Get<ITestEmitter>(sourceId), cancellation.Token);
        await cancellation.CancelAsync();
        // Wait on the completion itself, not a 5 s race against it: a stalled runner must not turn "not yet" into "never".
        try { await subscription.Completion; } catch (OperationCanceledException) { }
        return subscription.Completion.IsCanceled;
    }

    private async Task Drain()
    {
        if (_subscription is null) { return; }
        await foreach (var number in _subscription.ReadAllAsync())
        {
            await PublishAsync(new Heard(number.Value));
        }
    }
}

[GenerateSerializer]
public sealed record MembersChanged : Signal;
[Alias("test.registry")]
public interface ITestRegistry : INeuron
{
    Task Set(string[] members);
    Task<string[]> Members();
}
[GrainType("test-registry")]
public sealed class TestRegistry : Neuron, ITestRegistry
{
    private string[] _members = [];
    public async Task Set(string[] members) { _members = members; await PublishAsync(new MembersChanged()); }
    public Task<string[]> Members() => Task.FromResult(_members);
}
