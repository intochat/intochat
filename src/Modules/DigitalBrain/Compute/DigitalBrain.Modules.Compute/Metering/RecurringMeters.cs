using DigitalBrain.Framework.Metering;

namespace DigitalBrain.Compute.Metering;

// A recurring meter accrues for a period without a user intent: storage and subscriptions are
// billed from the meter alone. The synthetic intent id keys one accrual per brain, meter and period.
internal static class RecurringMeters
{
    internal const string CostBasis = "recurring";

    internal static MeterEvent Accrue(
        string brainId,
        string meterId,
        decimal quantity,
        string unit,
        DateTimeOffset now,
        string? appId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(meterId);
        var period = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return new MeterEvent
        {
            IntentId = $"recurring:{brainId}:{meterId}:{period}",
            MeterId = meterId,
            Step = period,
            BrainId = brainId,
            Quantity = quantity,
            Unit = unit,
            Source = MeterSource.StorageSampler,
            AppId = appId,
            CostBasis = CostBasis,
            OccurredAt = now,
        };
    }
}
