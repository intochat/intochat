using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Compute;

// The platform cost meter: prices a quantity through the registered IPriceLists (first non-null
// wins), applies the funding rule and the platform fee, gates integration spend against the trace's
// budget, then charges the payer's ledger and records the brain's usage. With no current trace the
// cost lands on the caller's brain under an untraced trace.
internal sealed class CostMeter(
    IEnumerable<IPriceList> priceLists,
    AuthorityNeurons neurons,
    IOptions<FeeOptions> fees,
    ILogger<CostMeter> logger)
    : ICostMeter
{
    public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken, CostSubject? subject = null)
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
            Synapse = subject?.Synapse,
            App = subject?.App,
            Neuron = subject?.Neuron,
            At = DateTimeOffset.UtcNow,
        };

        // A redelivered cost is charged once (the ledger dedups on TraceId/CostId); only a fresh charge counts as usage.
        // The budget and the charge hold the call; the usage record is one-way and never does.
        var fresh = true;
        if (micros > 0)
        {
            var payer = trace.PayerAccountId;
            if (trace.BudgetMicros is { } budget
                && !await neurons.TraceBudget(trace.TraceId).TrySpend(micros, budget))
            { throw new BudgetExhaustedException(trace.TraceId); }
            if (funding.Kind == FundingKind.Circle) { payer = await DrawFromCircle(funding.CircleKey!, trace.PayerAccountId, micros, trace.TraceId); }
            fresh = await neurons.Ledger(payer).Charge(cost);
        }
        if (fresh) { await neurons.Usage(trace.BrainId).Add(cost, origin); }
        return cost;
    }

    // A circle-funded call is paid by the circle's creator, within the circle share of the creator's budget; the
    // spender is a member account (T26-2, T26-11).
    private async Task<string> DrawFromCircle(string circleKey, string spenderAccount, long micros, string traceId)
    {
        return await neurons.CircleShare(circleKey).TryDraw(spenderAccount, micros)
            ?? throw new BudgetExhaustedException(traceId);
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
