using System.Collections.Concurrent;
using IntoChat.OS.Contracts.Compute;
using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

// A call that produces several facts makes them durable together: one journal append, each fact under its own trace.
public sealed class PublishManyFacts(FrameworkHost host)
{
    private static readonly Trace First = new() { TraceId = "4bf92f3577b34da6a3ce929d0e0e4736", PayerAccountId = "acct-1", BrainId = "brain-1", Origin = new NeuronId("brain-1/edge") };
    private static readonly Trace Second = new() { TraceId = "0af7651916cd43dd8448eb211c80319c", PayerAccountId = "acct-2", BrainId = "brain-1", Origin = new NeuronId("brain-1/edge") };

    [Trait("host", "own")]
    [Fact]
    public async Task ManySignalsOfOneCallAreJournaledInOneAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        var journal = new CountingJournal();
        await using var brain = await TestBrain.Create().ConfigureServices(services => services.AddSingleton<ISignalJournal>(journal)).StartAsync(ct);
        var source = brain.Get<IManySource>("many-source");
        var observer = brain.Get<IManyObserver>("subscriber/many-observer");
        await source.Watch(observer);

        await source.Emit([First, Second, null]);

        var (_, signals) = Assert.Single(journal.Appends, append => append.Publisher == source.GetGrainId());
        Assert.Equal(3, signals.Length);
        Assert.All(signals, signal => Assert.Equal(source.GetGrainId().ToString(), signal.Publisher.Name));
        Assert.Equal(3, (await Heard(observer, 3, ct)).Length);
    }

    [Fact]
    public async Task EachSignalKeepsTheTraceItWasPublishedWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<IManySource>($"many-source-{id}");
        var observer = brain.Get<IManyObserver>($"subscriber/many-observer-{id}");
        await source.Watch(observer);
        var current = TraceContext.Begin("acct-0", "brain-1", new NeuronId("brain-1/button"));
        using var _ = TraceContext.Enter(current);

        await source.Emit([First, Second, null]);

        Assert.Equal([First.TraceId, Second.TraceId, current.TraceId], await Heard(observer, 3, ct));
    }

    [Fact]
    public async Task AUserSetTraceIsStillOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var id = Guid.NewGuid().ToString("N");
        var source = brain.Get<IManySource>($"many-source-{id}");
        var observer = brain.Get<IManyObserver>($"subscriber/many-observer-{id}");
        await source.Watch(observer);
        var forged = new Trace { TraceId = "forged", PayerAccountId = "mallory", BrainId = "mallory", Origin = new NeuronId("mallory/edge") };

        await source.EmitForged(forged, [First, null]);

        Assert.Equal([First.TraceId, null], await Heard(observer, 2, ct));
    }

    private static Task<string?[]> Heard(IManyObserver observer, int count, CancellationToken ct)
        => TestWait.UntilAsync(_ => observer.Read(), traces => traces.Length >= count, TimeSpan.FromSeconds(15), ct);

    private sealed class CountingJournal : ISignalJournal
    {
        private readonly MemorySignalJournal _inner = new(Options.Create(new MemorySignalJournalOptions()));

        public ConcurrentQueue<(GrainId Publisher, Signal[] Signals)> Appends { get; } = new();

        public Task<long> Append(GrainId publisher, Signal[] signals)
        {
            Appends.Enqueue((publisher, signals));
            return _inner.Append(publisher, signals);
        }

        public Task<JournaledSignal[]> ReadSince(string brainId, long since, int limit) => _inner.ReadSince(brainId, since, limit);

        public Task<PartSignals?> SignalsOf(GrainId publisher, long since) => _inner.SignalsOf(publisher, since);
    }
}

[Alias("test.many-source")]
[Public]
public interface IManySource : INeuron
{
    Task Emit(Trace?[] traces);
    Task EmitForged(Trace forged, Trace?[] traces);
}

[Alias("test.many-observer")]
public interface IManyObserver : INeuron, INeuronObserver { Task<string?[]> Read(); }

[GrainType("test-many-source")]
public sealed class ManySource : Neuron, IManySource
{
    public Task Emit(Trace?[] traces) => PublishAsync([.. traces.Select(trace => ((Signal)new TraceSignal(), trace))]);

    public Task EmitForged(Trace forged, Trace?[] traces)
        => PublishAsync([.. traces.Select(trace => ((Signal)(new TraceSignal() with { Trace = forged }), trace))]);
}

[GrainType("test-many-observer")]
public sealed class ManyObserver : Neuron, IManyObserver
{
    private readonly List<string?> _traces = [];
    private long _cursor;

    public Task<string?[]> Read() => Task.FromResult<string?[]>([.. _traces]);

    public async Task Nudge(SignalNudge nudge)
    {
        (var signals, _cursor) = await NudgeReader.ReadAfter(NudgeReader.Source(GrainFactory, nudge), _cursor);
        _traces.AddRange(signals.Select(signal => signal.Trace?.TraceId));
    }
}
