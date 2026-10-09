using DigitalBrain.Framework;

namespace DigitalBrain.Compute.Allowances;

[GenerateSerializer, Alias("compute.allowance-ledger-state")]
internal sealed record AllowanceLedgerState
{
    [Id(0)] public List<Allowance> Allowances { get; init; } = [];
    [Id(1)] public List<AllowanceReservation> Reservations { get; init; } = [];
    [Id(2)] public List<PendingApproval> Pending { get; init; } = [];
    [Id(3)] public LimitPolicy Limits { get; set; } = new();
    [Id(4)] public LimitAlert HighestAlert { get; set; }
    [Id(5)] public DateTimeOffset? HardStoppedAt { get; set; }
}

// One activation per account is the single writer for allowances, reservations, pending approvals
// and limits. The reservation id is derived from the intent and operation, so a retried or
// duplicated call reserves once and chaos never double-charges.
[GrainType("compute-allowance-ledger")]
internal sealed class AllowanceLedgerNeuron : Neuron<AllowanceLedgerState>, IAllowanceLedger
{
    private readonly IPersistentState<AllowanceLedgerState> store;
    private readonly IPriceBook priceBook;
    private bool writePending;

    public AllowanceLedgerNeuron(
        [PersistentState("allowance-ledger", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<AllowanceLedgerState> store,
        IPriceBook priceBook)
        : base(store)
    {
        this.store = store;
        this.priceBook = priceBook;
    }

    private string AccountId => this.GetPrimaryKeyString();

    public Task<decimal> ReadSettledAsync(string? brainId = null, string? intentId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot.Reservations.Where(item => item.Settled
            && (brainId is null || item.BrainId == brainId)
            && (intentId is null || item.IntentId == intentId)).Sum(item => item.SettledCompute));
    }

    public Task<decimal> ReadReservedAsync(string? brainId = null, string? intentId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot.Reservations.Where(item => !item.Settled
            && (brainId is null || item.BrainId == brainId)
            && (intentId is null || item.IntentId == intentId)).Sum(item => item.ReservedCompute));
    }

    public async Task<AllowanceDecision> AuthorizeAsync(AllowanceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireAccount(request.AccountId);
        // A failed write leaves this activation's mutations in memory. Retry persistence
        // before any cached reservation or unchanged decision can be returned.
        if (writePending) { await PersistAsync(); }
        var now = DateTimeOffset.UtcNow;
        var next = Snapshot;

        var reservationId = AllowanceRules.ReservationId(request);
        var existing = next.Reservations.Find(reservation => string.Equals(reservation.ReservationId, reservationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return new AllowanceDecision { Allowed = true, ReservationId = existing.ReservationId, AllowanceId = existing.AllowanceId };
        }

        var context = new AllowanceSnapshot([.. next.Allowances], [.. next.Reservations], next.Limits, priceBook.Version, next.HighestAlert, next.HardStoppedAt);
        var result = AllowanceRules.Evaluate(request, context, now);
        var decision = result.Decision;
        var changed = false;

        if (decision.Allowed && request.EstimatedCompute > 0)
        {
            next.Reservations.Add(new AllowanceReservation
            {
                ReservationId = reservationId,
                AllowanceId = decision.AllowanceId ?? "none",
                ReservedCompute = request.EstimatedCompute,
                OccurredAt = now,
                IntentId = request.IntentId,
                BrainId = request.BrainId,
                AppId = request.AppId,
                Operation = request.Operation,
            });
            changed = true;
        }

        if (decision.Denial == AllowanceDenial.MissingAllowance)
        {
            changed |= AddPending(next, request, decision.ApprovalId!, now);
        }

        if (result.HardStop && next.HardStoppedAt is null) { next.HardStoppedAt = now; changed = true; }
        if (result.Alert > next.HighestAlert) { next.HighestAlert = result.Alert; changed = true; }
        // Authorization still evaluates the current ledger on every call. Persist only when
        // the decision changes durable state; ordinary reads do not need a storage round trip.
        if (changed) { await PersistAsync(); }
        return decision;
    }

    public async Task<Allowance> GrantAsync(Allowance allowance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(allowance);
        var next = Snapshot;
        var stored = allowance with { AccountId = AccountId };
        next.Allowances.RemoveAll(existing => string.Equals(existing.AllowanceId, stored.AllowanceId, StringComparison.Ordinal));
        next.Allowances.Add(stored);
        next.Pending.RemoveAll(pending => string.Equals(pending.ApprovalId, stored.AllowanceId, StringComparison.Ordinal));
        await PersistAsync();
        return stored;
    }

    public Task<PendingApproval[]> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PendingApproval[]>([.. Snapshot.Pending]);
    }

    public async Task<Allowance> ApproveAsync(string approvalId, ApprovalLevel level, AllowanceScope scope, decimal limitCompute, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        var next = Snapshot;
        var pending = next.Pending.Find(item => string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No pending approval '{approvalId}'.");
        next.Pending.RemoveAll(item => string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal));
        var allowance = new Allowance
        {
            AllowanceId = approvalId,
            AccountId = AccountId,
            BrainId = pending.BrainId,
            AppId = pending.AppId,
            Operation = level == ApprovalLevel.PerOperation ? pending.Operation : null,
            Level = level,
            Scope = scope,
            LimitCompute = limitCompute,
            MonthlyLimitCompute = level == ApprovalLevel.InstallConsent ? limitCompute : 0m,
            Permissions = pending.Permissions,
            PriceBookVersion = priceBook.Version,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        next.Allowances.Add(allowance);
        await PersistAsync();
        return allowance;
    }

    public async Task DenyAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = Snapshot;
        if (next.Pending.RemoveAll(item => string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal)) > 0)
        {
            await PersistAsync();
        }
    }

    public async Task<AllowanceReservation> SettleAsync(string reservationId, decimal actualCompute, FailureClass failure, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        var next = Snapshot;
        var index = next.Reservations.FindIndex(reservation => string.Equals(reservation.ReservationId, reservationId, StringComparison.Ordinal));
        if (index < 0) { throw new InvalidOperationException($"No reservation '{reservationId}'."); }
        var reservation = next.Reservations[index];
        if (reservation.Settled) { return reservation; }

        var spentElsewhere = next.Reservations.Where(item => !ReferenceEquals(item, reservation)).Sum(AllowanceRules.Effective);
        var cap = EffectiveCap(next.Limits, reservation);
        var settled = AllowanceRules.ShouldCharge(failure)
            ? AllowanceRules.CapSettlement(actualCompute, spentElsewhere, cap)
            : 0m;
        var updated = reservation with { Settled = true, SettledCompute = settled, Failure = failure };
        next.Reservations[index] = updated;
        if (cap > 0 && spentElsewhere + settled >= cap) { next.HardStoppedAt ??= DateTimeOffset.UtcNow; }
        await PersistAsync();
        return updated;
    }

    public async Task<LimitPolicy> SetLimitsAsync(LimitPolicy policy, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(policy);
        var next = Snapshot;
        next.Limits = policy;
        await PersistAsync();
        return policy;
    }

    public Task<LimitReport> ReadLimitsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot;
        var spent = snapshot.Reservations.Sum(AllowanceRules.Effective);
        var limit = snapshot.Limits.AccountLimitCompute;
        return Task.FromResult(new LimitReport
        {
            AccountId = AccountId,
            SpentCompute = spent,
            LimitCompute = limit,
            HighestAlert = snapshot.HighestAlert,
            HardStoppedAt = snapshot.HardStoppedAt,
            ReadExportGraceUntil = snapshot.HardStoppedAt is { } stopped ? stopped + AllowanceRules.ReadExportGrace : null,
        });
    }

    public Task<Allowance[]> ReadAllowancesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Allowance[]>([.. Snapshot.Allowances]);
    }

    public Task<AllowanceReservation[]> ReadReservationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<AllowanceReservation[]>([.. Snapshot.Reservations]);
    }

    private async Task PersistAsync()
    {
        writePending = true;
        await store.WriteStateAsync().ConfigureAwait(true);
        writePending = false;
    }

    private static bool AddPending(AllowanceLedgerState next, AllowanceRequest request, string approvalId, DateTimeOffset now)
    {
        if (next.Pending.Exists(item => string.Equals(item.ApprovalId, approvalId, StringComparison.Ordinal))) { return false; }
        next.Pending.Add(new PendingApproval
        {
            ApprovalId = approvalId,
            BrainId = request.BrainId,
            AppId = request.AppId,
            Operation = request.Operation,
            EstimatedCompute = request.EstimatedCompute,
            Reason = "Awaiting an allowance decision.",
            RequestedAt = now,
            IntentId = request.IntentId,
            Permissions = request.SemanticTypeIds.ToArray(),
        });
        return true;
    }

    private static decimal EffectiveCap(LimitPolicy policy, AllowanceReservation reservation)
    {
        var caps = new List<decimal>();
        if (policy.AccountLimitCompute > 0) { caps.Add(policy.AccountLimitCompute); }
        if (policy.BrainLimitCompute > 0) { caps.Add(policy.BrainLimitCompute); }
        if (policy.AppLimitCompute > 0 && reservation.AppId is { Length: > 0 }) { caps.Add(policy.AppLimitCompute); }
        if (policy.RunLimitCompute > 0 && reservation.IntentId is { Length: > 0 }) { caps.Add(policy.RunLimitCompute); }
        return caps.Count == 0 ? 0m : caps.Min();
    }

    private static void RequireAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) { throw new ArgumentException("A paid call needs an account id.", nameof(accountId)); }
    }
}
