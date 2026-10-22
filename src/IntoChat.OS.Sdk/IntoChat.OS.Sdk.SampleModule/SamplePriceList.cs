using System;
using IntoChat.OS.Framework.Compute;

namespace IntoChat.OS.Sdk.SampleModule;

// Illustrative rates only: the sample has no real provider and makes no tariff claim.
public sealed class SamplePriceList : IPriceList
{
    public long? MicrosPerUnit(CostKind kind, string item)
        => kind == CostKind.ExternalProvider && item == "sample.read" ? 0 : null;

    public PriceQuote Quote(CostKind kind, string item, string unit)
    {
        var declared = kind == CostKind.ExternalProvider && item is "sample.read" or "sample.refresh";
        return new()
        {
            Unit = declared ? "request" : unit,
            MicrosPerUnit = MicrosPerUnit(kind, item),
            TariffVersion = declared ? "sample-v1" : null,
            EffectiveFrom = declared ? new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero) : null,
            Source = declared ? "https://example.test/pricing" : null,
            ChargePolicy = declared ? ExternalChargePolicy.EveryDispatchedAttempt : ExternalChargePolicy.Unspecified,
        };
    }
}
