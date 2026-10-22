using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Sdk.Compute;

namespace IntoChat.OS.Authority.Compute;

// The platform cost meter: prices a quantity through the registered IPriceLists (first non-null
// wins), applies the funding rule and the platform fee, gates integration spend against the trace's
// budget, then charges the payer's ledger and records the brain's usage. With no current trace the
// cost lands on the caller's brain under an untraced trace.
internal sealed class CostMeter(
    IEnumerable<IPriceList> priceLists,
    AuthorityNeurons neurons,
    IOptions<FeeOptions> fees,
    ILogger<CostMeter> logger,
    ModulePriceListRegistry registry,
    IServiceProvider services)
    : ICostMeter
{
    public async ValueTask<ExternalCallHandle> BeginExternalCallAsync(ExternalCallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var trace = TraceContext.Current ?? throw new UntrustedCallerException("External calls require a current trusted trace.");
        var caller = CallerContextStamper.Require();
        if (!CallerContextStamper.IsTrusted(caller) || caller.BrainId != trace.BrainId || caller.AccountId != trace.PayerAccountId)
        { throw new UntrustedCallerException("External trace attribution must match the stamped caller."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvocationId);
        if (request.CostId.Length > 128 || request.InvocationId.Length > 128 || request.Quantity <= 0) { throw new ArgumentException("Invalid external call identity or quantity.", nameof(request)); }
        var descriptor = registry.GetOperation(request.Operation);
        if (descriptor.Unit != request.Unit) { throw new ArgumentException("External quantity unit does not match its declaration.", nameof(request)); }
        var owner = registry.Resolve(request.Operation);
        var list = (IPriceList)services.GetRequiredService(owner.PriceListType);
        var quote = list.Quote(CostKind.ExternalProvider, request.Operation, descriptor.Unit);
        if (quote.Unit != descriptor.Unit || quote.MicrosPerUnit < 0 || quote.ChargePolicy == ExternalChargePolicy.Unspecified || !Enum.IsDefined(quote.ChargePolicy))
        { throw new InvalidOperationException("External operation has an invalid price quote."); }
        var fee = fees.Value.PlatformPercent;
        if (fee < 0) { throw new InvalidOperationException("Platform fee must be nonnegative."); }
        ValidateFunding(request.Funding);
        var maximumList = quote.MicrosPerUnit is { } rate ? checked((long)Math.Ceiling(rate * request.Quantity)) : (long?)null;
        var maximum = quote.ChargePolicy == ExternalChargePolicy.Informational ? 0 : Charged(maximumList ?? 0, request.Funding, fee);
        return await neurons.Compute(trace.BrainId).Begin(new(trace, request, quote, fee, maximumList, maximum));
    }

    public async ValueTask MarkExternalCallDispatchStartedAsync(ExternalCallHandle handle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await HandleOwner(handle).MarkDispatchStarted(handle);
    }

    public async ValueTask<ExternalCallCompletion> CompleteExternalCallAsync(ExternalCallHandle handle, ExternalCallOutcome outcome, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await HandleOwner(handle).Observe(handle, outcome);
    }

    private IBrainComputeGrain HandleOwner(ExternalCallHandle handle)
    {
        var brain = BrainComputeGrain.HandleBrain(handle);
        if (TraceContext.Current is { } current && current.BrainId != brain) { throw new UnauthorizedAccessException("External handle belongs to another brain."); }
        return neurons.Compute(brain);
    }

    public async ValueTask<Cost> RecordAsync(CostKind kind, string item, decimal quantity, string unit, Funding funding,
        string costId, CancellationToken cancellationToken, CostSubject? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(costId);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);
        ValidateFunding(funding);
        var current = TraceContext.Current;
        var origin = current?.Origin.ToString() ?? "untraced";
        var trace = current ?? BeginUntraced();

        var listMicros = Price(kind, item, quantity);
        var micros = Charged(listMicros ?? 0, funding, fees.Value.PlatformPercent);
        if (listMicros is null) { logger.LogDebug("Unpriced {Kind} {Item}", kind, item); }

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
            ListPriceMicros = listMicros,
            PricingStatus = listMicros is null ? PricingStatus.Unpriced : PricingStatus.Known,
            FundingKind = funding.Kind,
        };

        return await neurons.Compute(trace.BrainId).Record(trace, cost, funding, origin);
    }

    private long? Price(CostKind kind, string item, decimal quantity)
    {
        foreach (var list in priceLists)
        {
            if (list.MicrosPerUnit(kind, item) is { } microsPerUnit)
            {
                if (microsPerUnit < 0) { throw new InvalidOperationException("Price must be nonnegative."); }
                return checked((long)Math.Ceiling(microsPerUnit * quantity));
            }
        }
        return null;
    }

    private static long Charged(long listMicros, Funding funding, int percent)
    {
        if (percent < 0) { throw new InvalidOperationException("Platform fee must be nonnegative."); }
        return funding.Kind is FundingKind.Local or FundingKind.OwnConnection ? 0
            : checked(listMicros + (long)Math.Ceiling(listMicros * (decimal)percent / 100));
    }

    private static void ValidateFunding(Funding funding)
    {
        if (!Enum.IsDefined(funding.Kind) || funding.Kind == FundingKind.Circle && string.IsNullOrWhiteSpace(funding.CircleKey))
        { throw new ArgumentException("Invalid funding selection.", nameof(funding)); }
    }

    private static Trace BeginUntraced()
    {
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new UntrustedCallerException("A cost was recorded without a trace or a stamped caller."); }
        return TraceContext.Begin(caller.AccountId, caller.BrainId, new NeuronId($"{caller.BrainId}/edge"));
    }
}
