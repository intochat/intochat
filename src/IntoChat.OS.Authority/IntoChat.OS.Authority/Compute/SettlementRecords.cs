using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Authority.Compute;

[GenerateSerializer, Alias("compute.reservation-key")]
internal sealed record ReservationKey([property: Id(0)] string TraceId, [property: Id(1)] string CostId,
    [property: Id(2)] string? BrainId = null);

[GenerateSerializer, Alias("compute.funding-reservation")]
internal sealed record FundingReservation(
    [property: Id(0)] ReservationKey Key,
    [property: Id(1)] long MaximumMicros,
    [property: Id(2)] long? SettledMicros = null,
    [property: Id(3)] string? Payer = null,
    [property: Id(4)] string? Member = null);

[GenerateSerializer, Alias("compute.ledger-settlement")]
internal sealed record LedgerSettlement([property: Id(0)] ReservationKey Key, [property: Id(1)] Cost Cost);
