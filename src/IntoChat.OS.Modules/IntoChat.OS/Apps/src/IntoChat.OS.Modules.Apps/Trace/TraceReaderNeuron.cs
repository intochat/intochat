using System.Reflection;
using System.Text.Json;
using DigitalBrain;

namespace IntoChat.OS.Modules.Apps.Trace;

// Forwards to the brain's signal history. Payloads are shown as journaled: the agent redacts tool arguments and results before publishing.
[DigitalBrain.Neuron("trace.reader"), StatelessNeuron]
public sealed class TraceReaderNeuron : DigitalBrain.Neuron, ITraceReader
{
    public const int PayloadLimit = 2000;

    public async Task<TracePage> ReadTrace(string traceId, long since, int limit)
    {
        var page = await Context.History.ReadTrace(traceId, since, limit);
        return new TracePage([.. page.Signals.Select(Entry)]);
    }

    // Cuts at PayloadLimit UTF-16 units without splitting a surrogate pair.
    public static string Trim(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length <= PayloadLimit) { return payload; }
        var cut = char.IsHighSurrogate(payload[PayloadLimit - 1]) ? PayloadLimit - 1 : PayloadLimit;
        return payload[..cut] + "…";
    }

    public static TraceEntry Entry(JournaledSignal journaled)
    {
        ArgumentNullException.ThrowIfNull(journaled);
        var type = journaled.Signal.GetType();
        string payload;
        try { payload = JsonSerializer.Serialize(journaled.Signal, type, JsonSerializerOptions.Web); }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException) { payload = type.Name; }
        payload = Trim(payload);
        return new TraceEntry(journaled.Publisher.ToString(), journaled.Sequence, type.GetCustomAttribute<SignalAttribute>()?.Alias ?? type.Name,
            journaled.At, journaled.TraceId, journaled.CausationId, payload);
    }
}
