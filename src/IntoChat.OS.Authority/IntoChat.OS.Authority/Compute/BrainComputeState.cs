using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Authority.Compute;

[GenerateSerializer, Alias("compute.brain-state")]
internal sealed class BrainComputeState
{
    [Id(0)] public ExternalCallReceipt[] Calls { get; set; } = [];
    [Id(1)] public UsageProjection[] Outbox { get; set; } = [];
    [Id(2)] public long Sequence { get; set; }
    [Id(3)] public RetainedTrace[] Traces { get; set; } = [];
}

[GenerateSerializer, Alias("compute.retained-trace")]
internal sealed record RetainedTrace([property: Id(0)] string TraceId, [property: Id(1)] DateTimeOffset At);

[GenerateSerializer, Alias("compute.pinned-external-call")]
internal sealed record PinnedExternalCall(
    [property: Id(0)] Trace Trace, [property: Id(1)] ExternalCallRequest Request,
    [property: Id(2)] PriceQuote Quote, [property: Id(3)] int FeePercent,
    [property: Id(4)] long? MaximumListMicros, [property: Id(5)] long MaximumMicros);

[GenerateSerializer, Alias("compute.external-receipt")]
internal sealed record ExternalCallReceipt
{
    [Id(0)] public required ExternalCallHandle Handle { get; init; }
    [Id(1)] public required Trace Trace { get; init; }
    [Id(2)] public required Cost Cost { get; set; }
    [Id(3)] public required Funding Funding { get; init; }
    [Id(4)] public ExternalCallRequest? Request { get; init; }
    [Id(5)] public PriceQuote? Quote { get; init; }
    [Id(6)] public int FeePercent { get; init; }
    [Id(7)] public long MaximumMicros { get; init; }
    [Id(8)] public string? Payer { get; set; }
    [Id(9)] public bool BudgetReserved { get; set; }
    [Id(10)] public bool FundingReserved { get; set; }
    [Id(11)] public bool Admitted { get; set; }
    [Id(12)] public ExternalCallOutcome? Outcome { get; set; }
    [Id(13)] public bool LedgerSettled { get; set; }
    [Id(14)] public bool BudgetSettled { get; set; }
    [Id(15)] public bool FundingSettled { get; set; }
    [Id(16)] public bool Settled { get; set; }
    [Id(17)] public bool Acknowledged { get; set; }
    [Id(18)] public string Origin { get; init; } = "";
    [Id(19)] public bool AdmissionRejected { get; set; }
}

[GenerateSerializer, Alias("compute.usage-projection")]
internal sealed record UsageProjection(
    [property: Id(0)] long Sequence, [property: Id(1)] Cost Cost,
    [property: Id(2)] Cost? Previous, [property: Id(3)] string Origin);
