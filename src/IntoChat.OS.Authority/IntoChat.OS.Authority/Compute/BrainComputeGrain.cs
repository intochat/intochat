using System.Text;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Compute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Compute;

[GrainType("brain-compute")]
internal sealed class BrainComputeGrain(
    [PersistentState("brain-compute", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<BrainComputeState> store,
    AuthorityNeurons neurons, IOptions<ComputeOptions> options, TimeProvider clock, ILogger<BrainComputeGrain> logger)
    : Grain, IBrainComputeGrain, IRemindable
{
    private string Brain => this.GetPrimaryKeyString()[..^"/compute".Length];

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await this.RegisterOrUpdateReminder("accounting", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), cancellationToken);
        foreach (var receipt in store.State.Calls.Where(value => !value.Settled && value.Outcome is null && value.Cost.AttemptState == ExternalCallState.DispatchStarted))
        { Update(receipt, receipt.Cost with { AttemptState = ExternalCallState.Uncertain }); }
        await Reconcile();
    }

    public Task ReceiveReminder(string reminderName, TickStatus status) => Reconcile();

    public async Task<ExternalCallHandle> Begin(PinnedExternalCall pinned)
    {
        RequireBrain(pinned.Trace);
        await store.WriteStateAsync();
        var request = pinned.Request;
        var existing = Find(pinned.Trace.TraceId, request.CostId);
        if (existing is not null)
        {
            if (existing.Request != request || existing.Trace != pinned.Trace) { throw new InvalidOperationException("Conflicting external attempt identity."); }
            await Admit(existing);
            return existing.Handle;
        }
        await MakeRoom(pinned.Trace.TraceId);
        if (pinned.Quote.Status == PricingStatus.Unpriced && request.Funding.Kind is FundingKind.Integration or FundingKind.Circle)
        {
            var allowance = options.Value.AllowedUnpricedOperations.SingleOrDefault(value => value.Operation == request.Operation);
            if (allowance is null || store.State.Calls.Count(value => value.Trace.TraceId == pinned.Trace.TraceId && value.Request?.Operation == request.Operation) >= allowance.MaxAttemptsPerTrace)
            { throw new InvalidOperationException("Unpriced deployment operation is not admitted by a bounded policy."); }
        }
        var receipt = new ExternalCallReceipt
        {
            Handle = NewHandle(),
            Trace = pinned.Trace,
            Request = request,
            Funding = request.Funding,
            Quote = pinned.Quote,
            FeePercent = pinned.FeePercent,
            MaximumMicros = pinned.MaximumMicros,
            Payer = pinned.Trace.PayerAccountId,
            Origin = pinned.Trace.Origin.ToString(),
            Cost = new Cost
            {
                TraceId = pinned.Trace.TraceId,
                CostId = request.CostId,
                Kind = CostKind.ExternalProvider,
                Item = request.Operation,
                Quantity = request.Quantity,
                Unit = request.Unit,
                Micros = 0,
                At = clock.GetUtcNow(),
                Synapse = request.Subject?.Synapse,
                App = request.Subject?.App,
                Neuron = request.Subject?.Neuron,
                EstimatedListPriceMicros = pinned.MaximumListMicros,
                PricingStatus = pinned.Quote.Status,
                TariffVersion = pinned.Quote.TariffVersion,
                FundingKind = request.Funding.Kind,
                InvocationId = request.InvocationId,
                AttemptState = ExternalCallState.Reserved,
                ChargePolicy = pinned.Quote.ChargePolicy,
            },
        };
        store.State.Calls = [.. store.State.Calls, receipt];
        Enqueue(receipt, null);
        await store.WriteStateAsync();
        await Admit(receipt);
        await Drain();
        return receipt.Handle;
    }

    public async Task MarkDispatchStarted(ExternalCallHandle handle)
    {
        var receipt = Require(handle);
        await store.WriteStateAsync();
        if (!receipt.Admitted) { throw new InvalidOperationException("External attempt is not admitted."); }
        if (receipt.Cost.AttemptState == ExternalCallState.DispatchStarted) { return; }
        if (receipt.Cost.AttemptState != ExternalCallState.Reserved) { throw new InvalidOperationException("External attempt cannot be dispatched again."); }
        Update(receipt, receipt.Cost with { AttemptState = ExternalCallState.DispatchStarted });
        await store.WriteStateAsync();
        await Drain();
    }

    public async Task<ExternalCallCompletion> Observe(ExternalCallHandle handle, ExternalCallOutcome outcome)
    {
        var receipt = Require(handle);
        if (!Enum.IsDefined(outcome.Kind) || outcome.HttpStatusCode is < 100 or > 599 || outcome.ProviderRequestId?.Length > 200
            || outcome.ProviderRequestId?.Any(char.IsControl) == true)
        { throw new ArgumentException("Invalid external outcome metadata.", nameof(outcome)); }
        await store.WriteStateAsync();
        if (receipt.Outcome is { } prior && prior != outcome && prior.Kind is ExternalCallOutcomeKind.Succeeded or ExternalCallOutcomeKind.HttpFailed or ExternalCallOutcomeKind.CancelledBeforeDispatch)
        { throw new InvalidOperationException("Conflicting external outcome."); }
        if (receipt.Outcome is not null && outcome.Kind == ExternalCallOutcomeKind.CancelledBeforeDispatch && receipt.Outcome.Kind != outcome.Kind)
        { throw new InvalidOperationException("An observed dispatch cannot become a no-dispatch cancellation."); }
        if (receipt.Outcome is { } observation && observation != outcome && IsUncertain(observation.Kind) && IsUncertain(outcome.Kind))
        { throw new InvalidOperationException("An uncertain observation may only be replayed or refined to a definite outcome."); }
        if (receipt.Settled) { await Acknowledge(receipt); await Drain(); return Completion(receipt); }
        if (!receipt.Admitted) { throw new InvalidOperationException("External attempt is not admitted."); }
        if (receipt.Cost.AttemptState == ExternalCallState.Reserved && outcome.Kind != ExternalCallOutcomeKind.CancelledBeforeDispatch)
        { throw new InvalidOperationException("External attempt has no dispatch marker."); }
        receipt.Outcome = outcome;
        var uncertain = IsUncertain(outcome.Kind);
        var pending = uncertain && receipt.Quote!.ChargePolicy == ExternalChargePolicy.SuccessfulResponse;
        Update(receipt, receipt.Cost with
        {
            Outcome = outcome.Kind,
            ProviderRequestId = outcome.ProviderRequestId,
            AttemptState = pending ? ExternalCallState.Uncertain : ExternalCallState.OutcomeKnown,
        });
        await store.WriteStateAsync();
        if (!pending) { await Settle(receipt); }
        await Drain();
        return Completion(receipt);
    }

    public async Task<Cost> Record(Trace trace, Cost cost, Funding funding, string origin)
    {
        RequireBrain(trace);
        await store.WriteStateAsync();
        var receipt = Find(cost.TraceId, cost.CostId);
        if (receipt is not null)
        {
            var prior = receipt.Cost;
            if (receipt.Request is not null || receipt.Trace != trace || receipt.Funding != funding
                || prior.Kind != cost.Kind || prior.Item != cost.Item || prior.Quantity != cost.Quantity || prior.Unit != cost.Unit
                || prior.Synapse != cost.Synapse || prior.App != cost.App || prior.Neuron != cost.Neuron)
            { throw new InvalidOperationException("Conflicting recorded cost."); }
        }
        else
        {
            await MakeRoom(trace.TraceId);
            var payer = funding.Kind == FundingKind.Circle ? await neurons.CircleShare(funding.CircleKey!).ReadOwner() : trace.PayerAccountId;
            var alreadyCharged = cost.Kind != CostKind.ExternalProvider && funding.Kind is FundingKind.Integration or FundingKind.Circle && payer is not null
                && await neurons.Ledger(payer).ContainsLegacyCharge(cost.TraceId, cost.CostId);
            receipt = new()
            {
                Handle = NewHandle(),
                Trace = trace,
                Cost = cost,
                Funding = funding,
                MaximumMicros = cost.Micros,
                Payer = payer ?? trace.PayerAccountId,
                Origin = origin,
                Outcome = new(ExternalCallOutcomeKind.Succeeded),
                // Old retained charge identities prove the debit, but cannot reconstruct an atomic historical projection.
                Admitted = alreadyCharged,
                LedgerSettled = alreadyCharged,
                BudgetSettled = alreadyCharged,
                FundingSettled = alreadyCharged,
                Settled = alreadyCharged,
                Acknowledged = alreadyCharged,
            };
            store.State.Calls = [.. store.State.Calls, receipt];
            await store.WriteStateAsync();
        }
        await Admit(receipt);
        await Settle(receipt);
        await Drain();
        return receipt.Cost;
    }

    private async Task Admit(ExternalCallReceipt receipt)
    {
        if (receipt.AdmissionRejected) { throw new BudgetExhaustedException(receipt.Trace.TraceId); }
        if (receipt.Admitted || receipt.Settled) { return; }
        var key = Key(receipt);
        if (!receipt.BudgetReserved && receipt.Trace.BudgetMicros is { } budget && receipt.MaximumMicros > 0)
        {
            // Existing trace-only spend remains a conservative floor; new holds stay isolated by brain.
            var legacySpent = await neurons.TraceBudget(receipt.Trace.TraceId).ReadSpent();
            var remaining = legacySpent >= budget ? 0 : budget - legacySpent;
            if (!await neurons.TraceBudget(Brain, receipt.Trace.TraceId).TryReserve(key, receipt.MaximumMicros, remaining))
            {
                await Reject(receipt);
                throw new BudgetExhaustedException(receipt.Trace.TraceId);
            }
            receipt.BudgetReserved = true;
            await store.WriteStateAsync();
        }
        if (!receipt.FundingReserved && receipt.Funding.Kind == FundingKind.Circle)
        {
            string? payer;
            try { payer = await neurons.CircleShare(receipt.Funding.CircleKey!).TryReserve(key, receipt.Trace.PayerAccountId, receipt.MaximumMicros); }
            catch (UnauthorizedAccessException) { await Reject(receipt); throw; }
            if (payer is null)
            {
                await Reject(receipt);
                throw new BudgetExhaustedException(receipt.Trace.TraceId);
            }
            receipt.Payer = payer;
            receipt.FundingReserved = true;
            await store.WriteStateAsync();
        }
        receipt.Admitted = true;
        await store.WriteStateAsync();
    }

    private async Task Reject(ExternalCallReceipt receipt)
    {
        receipt.AdmissionRejected = true;
        receipt.Outcome = new(ExternalCallOutcomeKind.CancelledBeforeDispatch);
        await store.WriteStateAsync();
        await Settle(receipt);
    }

    private async Task Settle(ExternalCallReceipt receipt)
    {
        if (receipt.Settled) { await Acknowledge(receipt); return; }
        var final = FinalCost(receipt);
        var key = Key(receipt);
        if (!receipt.LedgerSettled)
        {
            if (final.Micros > 0) { await neurons.Ledger(receipt.Payer!).Settle(key, final); }
            receipt.LedgerSettled = true;
            await store.WriteStateAsync();
        }
        if (!receipt.BudgetSettled)
        {
            if (receipt.BudgetReserved) { await neurons.TraceBudget(Brain, receipt.Trace.TraceId).Settle(key, final.Micros); }
            receipt.BudgetSettled = true;
            await store.WriteStateAsync();
        }
        if (!receipt.FundingSettled)
        {
            if (receipt.FundingReserved) { await neurons.CircleShare(receipt.Funding.CircleKey!).Settle(key, final.Micros); }
            receipt.FundingSettled = true;
            await store.WriteStateAsync();
        }
        if (receipt.Request is null && !receipt.AdmissionRejected) { Enqueue(receipt, null); }
        else if (receipt.Request is null) { receipt.Cost = final; }
        else { Update(receipt, final); }
        receipt.Settled = true;
        await store.WriteStateAsync();
        await Acknowledge(receipt);
    }

    private async Task Acknowledge(ExternalCallReceipt receipt)
    {
        if (receipt.Acknowledged) { return; }
        if (receipt.Cost.Micros > 0) { await neurons.Ledger(receipt.Payer!).AcknowledgeSettlement(Key(receipt)); }
        if (receipt.BudgetReserved) { await neurons.TraceBudget(Brain, receipt.Trace.TraceId).Acknowledge(Key(receipt)); }
        if (receipt.FundingReserved) { await neurons.CircleShare(receipt.Funding.CircleKey!).Acknowledge(Key(receipt)); }
        receipt.Acknowledged = true;
        await store.WriteStateAsync();
    }

    private static Cost FinalCost(ExternalCallReceipt receipt)
    {
        if (receipt.AdmissionRejected) { return receipt.Cost with { Micros = 0, ListPriceMicros = null, EstimatedListPriceMicros = null, Outcome = ExternalCallOutcomeKind.CancelledBeforeDispatch, AttemptState = ExternalCallState.CancelledBeforeDispatch }; }
        if (receipt.Request is null) { return receipt.Cost; }
        var outcome = receipt.Outcome ?? throw new InvalidOperationException("External outcome is unknown.");
        var dispatched = outcome.Kind != ExternalCallOutcomeKind.CancelledBeforeDispatch;
        var priced = dispatched && (receipt.Quote!.ChargePolicy is ExternalChargePolicy.EveryDispatchedAttempt or ExternalChargePolicy.Informational
            || outcome.Kind == ExternalCallOutcomeKind.Succeeded);
        var list = receipt.Cost.PricingStatus == PricingStatus.Known ? priced ? receipt.Cost.EstimatedListPriceMicros : 0 : null;
        var micros = priced ? receipt.MaximumMicros : 0;
        return receipt.Cost with
        {
            Micros = micros,
            ListPriceMicros = list,
            EstimatedListPriceMicros = null,
            AttemptState = dispatched ? ExternalCallState.Settled : ExternalCallState.CancelledBeforeDispatch,
        };
    }

    public async Task Reconcile()
    {
        await store.WriteStateAsync();
        foreach (var receipt in store.State.Calls)
        {
            try
            {
                if (receipt.Settled) { await Acknowledge(receipt); }
                else
                {
                    if (!receipt.Admitted && !receipt.AdmissionRejected) { await Admit(receipt); }
                    if (receipt.Outcome is not null && receipt.Cost.AttemptState != ExternalCallState.Uncertain) { await Settle(receipt); }
                }
            }
            catch (Exception exception) { logger.LogWarning(exception, "Compute settlement remains pending for {TraceId}/{CostId}", receipt.Cost.TraceId, receipt.Cost.CostId); }
        }
        try { await Drain(); }
        catch (Exception exception) { logger.LogWarning(exception, "Compute projection remains pending for {Brain}", Brain); }
        await PruneExpired();
        await store.WriteStateAsync();
    }

    public async Task<ComputeTracePage> ReadTrace(string traceId, int take, string? continuationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        if (traceId.Contains('/') || traceId.Length > 128) { throw new ArgumentException("Invalid trace identity.", nameof(traceId)); }
        await Reconcile();
        var page = await neurons.TraceJournal(Brain, traceId).Read(take, continuationToken);
        if (continuationToken is not null) { return page; }
        var pending = store.State.Calls.Where(value => value.Trace.TraceId == traceId && !value.Settled).ToArray();
        if (page.Status is TraceDetailStatus.Expired or TraceDetailStatus.DetailUnavailable)
        {
            if (pending.Length > 0)
            {
                page = page with
                {
                    Rows = pending.Take(take).Select(value => value.Request is not null ? value.Cost : value.Cost with
                    {
                        Micros = 0,
                        ListPriceMicros = null,
                        EstimatedListPriceMicros = value.Cost.ListPriceMicros,
                        AttemptState = ExternalCallState.OutcomeKnown,
                    }).ToArray(),
                    PendingCount = pending.Length,
                    Status = TraceDetailStatus.Incomplete,
                };
            }
        }
        if (store.State.Outbox.Any(value => value.Cost.TraceId == traceId) || pending.Any(value => value.Request is null))
        {
            page = page with
            {
                Status = TraceDetailStatus.Incomplete,
                PendingCount = Math.Max(page.PendingCount, pending.LongLength),
                ContinuationToken = null,
            };
        }
        return page;
    }

    private async Task Drain()
    {
        while (store.State.Outbox.FirstOrDefault() is { } projection)
        {
            await neurons.TraceJournal(Brain, projection.Cost.TraceId).Append(projection.Cost, projection.Sequence);
            await neurons.Usage(Brain).Apply(projection);
            store.State.Outbox = store.State.Outbox[1..];
            await store.WriteStateAsync();
        }
    }

    private void Update(ExternalCallReceipt receipt, Cost cost)
    {
        var previous = receipt.Cost;
        if (previous == cost) { return; }
        receipt.Cost = cost;
        Enqueue(receipt, previous);
    }

    private void Enqueue(ExternalCallReceipt receipt, Cost? previous)
    {
        var sequence = checked(++store.State.Sequence);
        store.State.Outbox = [.. store.State.Outbox, new(sequence, receipt.Cost, previous, receipt.Origin)];
    }

    private async Task PruneExpired()
    {
        var cutoff = clock.GetUtcNow() - options.Value.SettledRetention;
        foreach (var trace in store.State.Traces.Where(value => value.At < cutoff).ToArray())
        {
            if (store.State.Calls.Any(value => value.Trace.TraceId == trace.TraceId && (!value.Settled || !value.Acknowledged))
                || store.State.Outbox.Any(value => value.Cost.TraceId == trace.TraceId)) { continue; }
            await neurons.TraceJournal(Brain, trace.TraceId).Expire();
            await neurons.TraceBudget(Brain, trace.TraceId).Expire();
            store.State.Calls = [.. store.State.Calls.Where(value => value.Trace.TraceId != trace.TraceId)];
            store.State.Traces = [.. store.State.Traces.Where(value => value.TraceId != trace.TraceId)];
        }
    }

    private async Task MakeRoom(string traceId)
    {
        await PruneExpired();
        if (store.State.Calls.Count(value => !value.Settled) >= options.Value.MaxUnsettledPerBrain)
        { throw new InvalidOperationException("Compute pending capacity is exhausted."); }
        if (store.State.Calls.Count(value => value.Trace.TraceId == traceId) >= options.Value.MaxTraceRevisions)
        { throw new InvalidOperationException("Compute retained trace capacity is exhausted."); }
        if (store.State.Traces.All(value => value.TraceId != traceId))
        {
            if (store.State.Traces.Length >= options.Value.MaxTracesPerBrain)
            {
                var oldest = store.State.Traces.OrderBy(value => value.At).FirstOrDefault(value =>
                    !store.State.Calls.Any(call => call.Trace.TraceId == value.TraceId && (!call.Settled || !call.Acknowledged))
                    && !store.State.Outbox.Any(projection => projection.Cost.TraceId == value.TraceId));
                if (oldest is null) { throw new InvalidOperationException("Compute retained trace capacity is exhausted."); }
                await neurons.TraceJournal(Brain, oldest.TraceId).Expire();
                await neurons.TraceBudget(Brain, oldest.TraceId).Expire();
                store.State.Calls = [.. store.State.Calls.Where(value => value.Trace.TraceId != oldest.TraceId)];
                store.State.Traces = [.. store.State.Traces.Where(value => value.TraceId != oldest.TraceId)];
            }
            store.State.Traces = [.. store.State.Traces, new(traceId, clock.GetUtcNow())];
        }
    }

    private ExternalCallReceipt? Find(string trace, string cost) => store.State.Calls.FirstOrDefault(value => value.Trace.TraceId == trace && value.Cost.CostId == cost);
    private ExternalCallReceipt Require(ExternalCallHandle handle) => store.State.Calls.FirstOrDefault(value => value.Handle == handle)
        ?? throw new InvalidOperationException("External call handle is unknown or expired.");
    private void RequireBrain(Trace trace) { if (trace.BrainId != Brain) { throw new UnauthorizedAccessException("Compute brain does not match the trace."); } }
    private ExternalCallHandle NewHandle() => new(Convert.ToBase64String(Encoding.UTF8.GetBytes(Brain)) + "." + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    internal static string HandleBrain(ExternalCallHandle handle)
    {
        try
        {
            if (handle.Value is null || handle.Value.Length > 512) { throw new FormatException(); }
            var parts = handle.Value.Split('.');
            if (parts.Length != 2 || parts[1].Length != 64) { throw new FormatException(); }
            return Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
        }
        catch (FormatException exception) { throw new ArgumentException("Invalid external call handle.", nameof(handle), exception); }
    }
    private static ReservationKey Key(ExternalCallReceipt receipt) => new(receipt.Cost.TraceId, receipt.Cost.CostId, receipt.Trace.BrainId);
    private static bool IsUncertain(ExternalCallOutcomeKind kind)
        => kind is ExternalCallOutcomeKind.TransportFailed or ExternalCallOutcomeKind.CancelledAfterDispatch or ExternalCallOutcomeKind.Uncertain;
    private static ExternalCallCompletion Completion(ExternalCallReceipt receipt) => new(receipt.Handle,
        !receipt.Settled ? ExternalAccountingStatus.Pending : receipt.Cost.AttemptState == ExternalCallState.CancelledBeforeDispatch
            ? ExternalAccountingStatus.CancelledBeforeDispatch : ExternalAccountingStatus.Settled, receipt.Cost);
}
