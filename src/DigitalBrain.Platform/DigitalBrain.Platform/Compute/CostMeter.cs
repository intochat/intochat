using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Platform.Compute;

// The platform cost meter: prices a quantity through the registered IPriceLists (first non-null
// wins), applies the funding rule and the platform fee, gates integration spend against the trace's
// budget, then charges the payer's ledger and records the brain's usage. With no current trace the
// cost lands on the caller's brain under an untraced trace.
internal sealed class CostMeter(
    IEnumerable<IPriceList> priceLists,
    IGrainFactory grains,
    IOptions<FeeOptions> fees,
    ILogger<CostMeter> logger)
    : ICostMeter
{
    public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(costId);
        var current = TraceContext.Current;
        var origin = current?.Origin.ToString() ?? "untraced";
        var trace = current ?? BeginUntraced();

        long micros;
        if (Price(kind, item, quantity) is { } listMicros)
        {
            micros = funding.Kind switch
            {
                FundingKind.Local or FundingKind.OwnConnection => 0,
                FundingKind.Integration or FundingKind.Circle => listMicros + FeeOn(listMicros),
                _ => throw new ArgumentOutOfRangeException(nameof(funding)),
            };
        }
        else
        {
            micros = 0;
            logger.LogDebug("Unpriced {Kind} {Item}", kind, item);
        }

        var cost = new Cost
        {
            TraceId = trace.TraceId,
            CostId = costId,
            Kind = kind,
            Item = item,
            Quantity = quantity,
            Unit = unit,
            Micros = micros,
            At = DateTimeOffset.UtcNow,
        };

        // A redelivered cost is charged once (the ledger dedups on TraceId/CostId); only a fresh charge counts as usage.
        var fresh = true;
        if (micros > 0)
        {
            var payer = trace.PayerAccountId;
            if (funding.Kind == FundingKind.Circle) { payer = await DrawFromCircle(funding.CircleKey!, micros, trace.TraceId); }
            else if (trace.BudgetMicros is { } budget
                && !await grains.GetGrain<ITraceBudgetGrain>(trace.TraceId).TrySpend(micros, budget))
            { throw new BudgetExhaustedException(trace.TraceId); }
            fresh = await grains.GetGrain<ILedgerGrain>(payer).Charge(cost);
        }
        if (fresh) { await grains.GetGrain<IUsageGrain>(trace.BrainId).Add(cost, origin); }
        return cost;
    }

    // A circle-funded call is paid by the circle owner, within the circle share of the owner budget.
    private async Task<string> DrawFromCircle(string circleKey, long micros, string traceId)
    {
        var share = grains.GetGrain<ICircleShareGrain>(circleKey);
        var owner = await share.Owner();
        if (owner is null || !await share.TryDraw(micros, await grains.GetGrain<ILedgerGrain>(owner).Balance()))
        { throw new BudgetExhaustedException(traceId); }
        return owner;
    }

    private long? Price(CostKind kind, string item, decimal quantity)
    {
        foreach (var list in priceLists)
        {
            if (list.MicrosPerUnit(kind, item) is { } microsPerUnit)
            { return (long)Math.Ceiling(microsPerUnit * quantity); }
        }
        return null;
    }

    private long FeeOn(long listMicros) => (long)Math.Ceiling(listMicros * (decimal)fees.Value.PlatformPercent / 100);

    private static Trace BeginUntraced()
    {
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new UntrustedCallerException("A cost was recorded without a trace or a stamped caller."); }
        return TraceContext.Begin(caller.AccountId, caller.BrainId, new NeuronId($"{caller.BrainId}/edge"));
    }
}