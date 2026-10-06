namespace DigitalBrain.Framework.Metering;

// What one provider call reports inside an intent scope. Modules define the concrete usage record
// (which may live in a *.Contracts project that cannot see the framework) and carry it in the envelope;
// the framework only accumulates entries and hands the whole batch to the sink.
public interface IIntentUsageEntry;

public sealed record IntentUsageEntry<T>(T Value) : IIntentUsageEntry where T : notnull;

public sealed record IntentUsageBatch(string IntentId, string? ScopeId, IIntentUsageEntry[] Usage);

// The narrow seam between the metering decorators and durable storage. The decorator owns reading the
// provider's usage; the sink owns accumulating it in the ambient intent scope and flushing the whole
// intent's batch to storage exactly once when the intent completes.
public interface IIntentUsageSink
{
    Task RecordAsync(string intentId, IIntentUsageEntry entry, CancellationToken cancellationToken = default);

    // Persists every entry the intent accumulated in one durable write. A batch is only written when the
    // intent recorded at least one provider call.
    Task FlushAsync(IntentUsageBatch intent, CancellationToken cancellationToken = default);
}
