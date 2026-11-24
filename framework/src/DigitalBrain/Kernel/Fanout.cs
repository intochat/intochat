namespace DigitalBrain.Kernel;

internal readonly record struct AliasMark(NeuronId Publisher, string Alias, Position Last);
internal readonly record struct PositionedSignal(Position Position, string Alias, JournaledSignal Journaled);
internal sealed record JournalPage(Position Floor, Position Last, IReadOnlyList<PositionedSignal> Signals);

internal interface IJournalView
{
    Position Last { get; }
    SubscriptionIndex Subscriptions { get; }
    Position FloorOf(NeuronId? publisher);
    Position LastOf(NeuronId publisher);
    IEnumerable<AliasMark> Marks(NeuronId? publisher);
    long SequenceAt(NeuronId publisher, Position position);
    Task<JournalPage> ReadAfter(Position after, NeuronId? publisher, int limit, CancellationToken ct);
}

internal interface INudge
{
    void Nudge(NeuronId subscriber, Position upTo);
    void Offer(NeuronId subscriber, NeuronId publisher, Signal signal) { }
}

/// <summary>Who a brain delivers its own signals as.</summary>
internal static class DeliveryCaller
{
    /// <summary>
    /// The caller a nudge to a subscriber of <paramref name="brain"/> carries. Delivery is the brain's own work: a publish that
    /// served a caller of another brain (whom the cross-brain wall refuses on the way to the subscriber), or no caller at all, is
    /// delivered as the brain's scheduler, so the visitor's identity never reaches a reaction. The brain's own caller stays.
    /// </summary>
    public static Access.Caller Of(Access.Caller? publishing, BrainId brain)
        => publishing is null || (publishing.Brain != brain && publishing.Actor is not Access.Actor.Kernel && !brain.IsSystem)
            ? Access.Caller.OfScheduler(brain)
            : publishing;
}

internal sealed class Fanout(
    IJournalView journal,
    INudge nudge,
    Func<string, Type?> typeOf,
    Action<NeuronId, IReadOnlyList<Signal>> listeners) : ISignalNudge
{
    public Task Appended(NeuronId publisher, IReadOnlyList<JournaledSignal> appended, Position last, CancellationToken ct)
    {
        listeners(publisher, [.. appended.Select(item => item.Signal)]);
        Appended(publisher, [.. appended.Select(item => SignalAliases.Of(item.Signal))], last);
        return Task.CompletedTask;
    }

    public void Appended(NeuronId publisher, IReadOnlyCollection<string> aliases, Position last)
    {
        foreach (var subscriber in journal.Subscriptions.Hearing(publisher, aliases, typeOf))
        {
            nudge.Nudge(subscriber, last);
        }
    }

    public Task Offered(NeuronId publisher, Signal signal, CancellationToken ct)
    {
        listeners(publisher, [signal]);
        foreach (var subscriber in journal.Subscriptions.Hearing(publisher, [SignalAliases.Of(signal)], typeOf))
        {
            nudge.Offer(subscriber, publisher, signal);
        }
        return Task.CompletedTask;
    }

    public int Reconcile()
    {
        var due = journal.Subscriptions.Due(journal, typeOf, Delivery.SkipFlush);
        foreach (var subscriber in due)
        {
            nudge.Nudge(subscriber, journal.Last);
        }
        return due.Count;
    }
}
