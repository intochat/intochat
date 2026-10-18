namespace IntoChat.OS;

// Why a piece of work runs: started at a trusted edge (a request, a connector event), inherited by
// everything published or called while handling it. Stamped by the runtime like Publisher, never
// set by user code; whoever it names pays for the work.
[GenerateSerializer, Alias("brain.trace"), Immutable]
public sealed record Trace
{
    [Id(0)] public required string TraceId { get; init; }
    [Id(1)] public required string PayerAccountId { get; init; }
    [Id(2)] public required string BrainId { get; init; }
    [Id(3)] public required NeuronId Origin { get; init; }
    [Id(4)] public long? BudgetMicros { get; init; }
}
