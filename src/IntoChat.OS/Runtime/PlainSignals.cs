using System.Reflection;
using System.Text.Json;

namespace IntoChat.OS.Runtime;

// Transitional (06-07, deleted with the Framework in 06-08): the os clients' road to the signals of plain [Neuron]s. Plain
// neurons publish DigitalBrain [Signal] records, which no observer, watch or os journal reader carries, so a client hears them
// through the runtime's per-session synapse neuron (04-13, ISessionSynapses). The kernel composition implements it.
public interface IPlainSignals
{
    // Session `session` of `brain` on the session synapse neuron. The session id is the caller's to issue (T-04-44): the
    // source's AdmitSynapse judges the session neuron "brain/runtime.session/{session}" as the subscriber.
    IPlainSession Session(string brain, string session);

    // The held signals of a plain publisher after publisher sequence `since`, read from its brain's log without activating it.
    Task<PlainSignalPage> Read(DigitalBrain.NeuronId publisher, long since, int limit, CancellationToken cancellationToken);
}

// One session on the session synapse neuron: Subscribe holds a synapse from a source for [Signal] aliases; Next hands the
// signals out in batches, resending a batch until a later Next acknowledges it; End removes every synapse of the session.
public interface IPlainSession
{
    /// <exception cref="UnauthorizedAccessException">The source refuses the session, or is in another brain.</exception>
    Task Subscribe(DigitalBrain.NeuronId source, string[] aliases);

    Task Unsubscribe(DigitalBrain.NeuronId source);

    // Null when nothing arrived within the runtime's long-poll timeout.
    Task<PlainSignalBatch?> Next(long acked, CancellationToken cancellationToken);

    Task End();
}

public sealed record PlainSignalBatch(long Number, IReadOnlyList<PlainSignalEntry> Signals);

// One signal as its [Signal] alias and JSON (web defaults). Alias GapAlias: history was trimmed past the session's cursor.
public sealed record PlainSignalEntry(string Source, long Sequence, string Alias, string Json, DateTimeOffset At)
{
    public const string GapAlias = "synapse.gap";

    public bool IsGap => Alias == GapAlias;
}

// Floor: everything at or below it is trimmed. Last: the publisher's last sequence.
public sealed record PlainSignalPage(long Floor, long Last, IReadOnlyList<PlainSignalEntry> Signals);

// An IIntoChatOS that also reaches plain neurons (the in-process client and the script connection); PlainNeuronClient
// dispatches to it.
public interface IPlainClient
{
    // A proxy of the plain contract for the neuron the os full key names.
    object GetPlain(Type contract, NeuronId id);

    // The [Signal] records T that source (a proxy from GetPlain) publishes after the subscription is registered.
    IAsyncEnumerable<T> OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken) where T : DigitalBrain.Signal;
}

public static class PlainSignalTypes
{
    // The frozen [Signal] alias of a DigitalBrain signal record.
    public static string AliasOf(Type signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return signal.GetCustomAttribute<DigitalBrain.SignalAttribute>(inherit: false)?.Alias
            ?? throw new ArgumentException($"{signal.Name} is not a [Signal] record.", nameof(signal));
    }

    public static T Decode<T>(string json) where T : DigitalBrain.Signal
        => JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? throw new JsonException($"A {typeof(T).Name} payload was null.");
}

// Transitional (06-07b, R4-17, deleted in 06-08): what a watcher of a plain publisher needs from IPlainSignals, shared by the
// shell's gRPC watch and the SSE feeds. The opening replay is the publisher's last held signal of an alias, read from the
// brain's log (the runtime keeps at least the newest RetentionOptions.HistoryWindowSignals per publisher); the live part is
// one session on the session synapse neuron.
public static class PlainWatch
{
    private const int Page = DigitalBrain.SignalPage.MaxLimit;

    // The newest held signal of alias that publisher published, or null when none is held.
    public static async Task<PlainSignalEntry?> Retained(IPlainSignals signals, DigitalBrain.NeuronId publisher, string alias, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        var head = await signals.Read(publisher, 0, 1, cancellationToken).ConfigureAwait(false);
        if (head.Last <= head.Floor) { return null; }
        // Newest page first, then one page further back each time, until the floor.
        var since = Math.Max(head.Floor, head.Last - Page);
        while (true)
        {
            var page = await signals.Read(publisher, since, Page, cancellationToken).ConfigureAwait(false);
            if (page.Signals.LastOrDefault(entry => entry.Alias == alias) is { } last) { return last; }
            if (since <= page.Floor) { return null; }
            since = Math.Max(page.Floor, since - Page);
        }
    }

    // The signals of the aliases source publishes after the subscription is registered; onSubscribed runs once it is (the place
    // for the opening replay). A batch is acknowledged when the consumer asks for more; disposal ends the session. Gaps are
    // yielded (PlainSignalEntry.IsGap) so the watcher can tell its reader to read again.
    /// <exception cref="UnauthorizedAccessException">The source refuses the session, or is in another brain.</exception>
    public static async IAsyncEnumerable<PlainSignalEntry> Live(IPlainSignals signals, DigitalBrain.NeuronId source, string[] aliases, string session,
        Func<Task> onSubscribed, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(onSubscribed);
        var channel = signals.Session(source.Brain.Value, session);
        try
        {
            await channel.Subscribe(source, aliases).WaitAsync(cancellationToken).ConfigureAwait(false);
            await onSubscribed().ConfigureAwait(false);
            long acked = 0;
            while (true)
            {
                var batch = await channel.Next(acked, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (batch is null) { continue; }
                foreach (var entry in batch.Signals) { yield return entry; }
                acked = batch.Number;
            }
        }
        finally
        {
            // Ending removes the session's synapses; one that fails to end expires after the runtime's SessionTtl.
            await channel.End().ConfigureAwait(false);
        }
    }
}
