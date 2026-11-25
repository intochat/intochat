using System.Security.Cryptography;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Grants;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Identity.Accounts;

/// <summary>
/// Bounded pure in-memory registry of active Day-0 ephemeral brains.
/// Zero orphan <c>try-*</c> accounts ever touch durable storage until promoted in-place via <see cref="BrainClaimService"/>.
/// Auto-evicts brains after <see cref="BrainResidency.Ephemeral.IdleTtl"/> of inactivity or under count/RAM pressure.
/// </summary>
internal sealed class EphemeralBrainRegistry : IBrainResidencyRegistry
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, EphemeralBrainRecord> records = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly IOptions<EphemeralBrainOptions> options;
    private readonly Func<IBrainResidencyStorage?> storageAccessor;
    private readonly AuthorityMirror? mirror;

    private IBrainResidencyStorage? Storage => storageAccessor();

    public EphemeralBrainRegistry(
        TimeProvider clock,
        IOptions<EphemeralBrainOptions> options,
        IBrainResidencyStorage? storage = null,
        AuthorityMirror? mirror = null)
        : this(clock, options, () => storage, mirror)
    {
    }

    internal EphemeralBrainRegistry(
        TimeProvider clock,
        IOptions<EphemeralBrainOptions> options,
        Func<IBrainResidencyStorage?> storageAccessor,
        AuthorityMirror? mirror = null)
    {
        this.clock = clock;
        this.options = options;
        this.storageAccessor = storageAccessor;
        this.mirror = mirror;
    }

    public int ActiveCount
    {
        get
        {
            lock (gate) { return records.Count; }
        }
    }

    /// <summary>
    /// Mints (or returns existing, when <paramref name="brainScopeId"/> is supplied) an ephemeral brain purely in RAM.
    /// Evicts expired or LRU entries if the active cap is reached.
    /// </summary>
    public EphemeralBrainRecord Mint(string? brainScopeId = null, BrainResidency.Ephemeral? residency = null)
    {
        var opts = options.Value;
        var effectiveResidency = residency ?? opts.ToResidency();
        var now = clock.GetUtcNow();
        List<string>? toEvict = null;
        EphemeralBrainRecord created;

        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(brainScopeId) && records.TryGetValue(brainScopeId, out var existing))
            {
                if (existing.Claiming || now - existing.LastActivityAt < existing.Residency.IdleTtl)
                {
                    existing.LastActivityAt = now;
                    return existing;
                }
                records.Remove(brainScopeId);
                (toEvict ??= []).Add(brainScopeId);
            }

            // Sweep expired entries before enforcing count/memory caps (never evict a brain mid-claim).
            foreach (var (id, rec) in records.ToArray())
            {
                if (!rec.Claiming && now - rec.LastActivityAt >= rec.Residency.IdleTtl)
                {
                    records.Remove(id);
                    (toEvict ??= []).Add(id);
                }
            }

            var maxActive = Math.Max(1, opts.MaxActiveBrains);
            while (records.Count >= maxActive)
            {
                var lru = records.Values.Where(r => !r.Claiming).OrderBy(r => r.LastActivityAt).FirstOrDefault();
                if (lru is null) { break; }
                records.Remove(lru.BrainId);
                (toEvict ??= []).Add(lru.BrainId);
            }

            var idToUse = string.IsNullOrWhiteSpace(brainScopeId)
                ? GuestSurface.BrainPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))
                : brainScopeId;

            if (!GuestSurface.IsGuestPrincipal(idToUse))
            {
                throw new ArgumentException($"Ephemeral brain ids must start with '{GuestSurface.BrainPrefix}'.", nameof(brainScopeId));
            }

            var member = new Member
            {
                PrincipalId = idToUse,
                AccountId = idToUse,
                BrainId = idToUse,
                DisplayName = "Guest",
                Role = MemberRole.Owner,
                JoinedAt = now,
            };
            var account = new Account
            {
                AccountId = idToUse,
                OwnerPrincipalId = idToUse,
                Name = "Guest",
                CreatedAt = now,
            };

            List<Grant> initialGrants =
            [
                .. ShippedGrants.ForAssistant(idToUse),
                // Allows the ephemeral visitor (stamped as Actor.App("guest")) to invoke desktop, assistant,
                // gallery, and built-in/mini-app neurons inside its own RAM brain (strictly owner-scoped to brain:{idToUse}).
                // Zero-token guard in GrantRules still blocks secrets, brain-keys, and oauth-exchange, and
                // IBrainCapabilityPolicy cleanly returns ClaimRequired for background automations / connectors / secrets.
                Grant.Tuple($"brain:{idToUse}", $"app:{GuestSurface.AppId}", "*", GrantRelations.Run),
                Grant.Tuple($"brain:{idToUse}", $"app:{GuestSurface.AppId}", "*", GrantRelations.View),
            ];

            created = new EphemeralBrainRecord(idToUse, effectiveResidency, account, member, now, initialGrants);
            records[idToUse] = created;
            mirror?.Publish(idToUse, created.ToAuthorityRow());
        }

        if (toEvict is not null)
        {
            var backingStorage = Storage;
            foreach (var evictedId in toEvict)
            {
                mirror?.Remove(evictedId);
                if (backingStorage is not null)
                {
                    _ = backingStorage.EvictAsync(evictedId, CancellationToken.None);
                }
            }
        }

        return created;
    }

    public bool Contains(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return false; }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            return records.TryGetValue(brainScopeId, out var rec) && (rec.Claiming || now - rec.LastActivityAt < rec.Residency.IdleTtl);
        }
    }

    public bool TryGet(string brainScopeId, out EphemeralBrainRecord record)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId))
        {
            record = null!;
            return false;
        }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (records.TryGetValue(brainScopeId, out var rec) && (rec.Claiming || now - rec.LastActivityAt < rec.Residency.IdleTtl))
            {
                record = rec;
                return true;
            }
        }
        record = null!;
        return false;
    }

    public bool TryGetAuthorityRow(string brainScopeId, out IdentityBrainRow row)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId))
        {
            row = null!;
            return false;
        }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (records.TryGetValue(brainScopeId, out var rec) && (rec.Claiming || now - rec.LastActivityAt < rec.Residency.IdleTtl))
            {
                rec.LastActivityAt = now;
                row = rec.ToAuthorityRow();
                return true;
            }
        }
        row = null!;
        return false;
    }

    public bool TryAddShippedGrants(string brainScopeId, Grant[] grants, out Grant[] added)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!records.TryGetValue(brainScopeId, out var rec) || (!rec.Claiming && now - rec.LastActivityAt >= rec.Residency.IdleTtl))
            {
                added = [];
                return false;
            }
            List<Grant> newlyAdded = [];
            foreach (var grant in grants)
            {
                var normalized = Grant.Normalize(grant);
                if (rec.Grants.Select(Grant.Normalize).Any(held => !held.Revoked
                    && string.Equals(held.Grantee, normalized.Grantee, StringComparison.Ordinal)
                    && string.Equals(held.Target, normalized.Target, StringComparison.Ordinal)
                    && string.Equals(held.Scope, normalized.Scope, StringComparison.Ordinal)))
                { continue; }
                var stored = normalized with { Owner = $"brain:{brainScopeId}", WorkspaceId = brainScopeId, GrantedAt = now };
                rec.Grants.Add(stored);
                newlyAdded.Add(stored);
            }
            if (newlyAdded.Count > 0)
            {
                mirror?.Publish(brainScopeId, rec.ToAuthorityRow());
            }
            added = [.. newlyAdded];
            return true;
        }
    }

    public Grant[] SnapshotCarriedGrants(string brainScopeId)
    {
        lock (gate)
        {
            if (!records.TryGetValue(brainScopeId, out var rec)) { return []; }
            return [.. rec.Grants.Where(g => !string.Equals(Grant.Normalize(g).Grantee, $"app:{GuestSurface.AppId}", StringComparison.Ordinal))];
        }
    }

    public bool TryGetResidency(string brainScopeId, out BrainResidency residency)
    {
        if (TryGetEphemeral(brainScopeId, out var ephemeral))
        {
            residency = ephemeral;
            return true;
        }
        residency = null!;
        return false;
    }

    public bool TryGetEphemeral(string brainScopeId, out BrainResidency.Ephemeral residency)
    {
        if (TryGet(brainScopeId, out var rec))
        {
            residency = rec.Residency;
            return true;
        }
        residency = null!;
        return false;
    }

    public BrainResidencySnapshot GetSnapshot(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId))
        {
            return BrainResidencySnapshot.Persistent();
        }
        var now = clock.GetUtcNow();
        var backingStorage = Storage;
        lock (gate)
        {
            if (records.TryGetValue(brainScopeId, out var rec))
            {
                var remainingTtl = rec.Residency.IdleTtl - (now - rec.LastActivityAt);
                var ttlSeconds = Math.Max(0L, (long)Math.Ceiling(remainingTtl.TotalSeconds));
                var remainingTurns = Math.Max(0, rec.Residency.MaxTrialTurns - rec.TurnsUsed);
                var usedBytes = backingStorage?.GetMemoryUsedBytes(brainScopeId) ?? 0;
                return new BrainResidencySnapshot(
                    IsEphemeral: true,
                    TtlSeconds: ttlSeconds,
                    RemainingTurns: remainingTurns,
                    MemoryUsedBytes: usedBytes,
                    MemoryLimitBytes: rec.Residency.MaxMemoryBytes);
            }
        }
        return BrainResidencySnapshot.Persistent(memoryUsedBytes: backingStorage?.GetMemoryUsedBytes(brainScopeId) ?? 0);
    }

    public void Touch(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return; }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (records.TryGetValue(brainScopeId, out var rec))
            {
                rec.LastActivityAt = now;
            }
        }
    }

    public bool TryConsumeTurn(string brainScopeId, out int remainingTurns)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId))
        {
            remainingTurns = int.MaxValue;
            return true;
        }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!records.TryGetValue(brainScopeId, out var rec))
            {
                remainingTurns = int.MaxValue;
                return true;
            }
            rec.LastActivityAt = now;
            if (rec.TurnsUsed >= rec.Residency.MaxTrialTurns)
            {
                remainingTurns = 0;
                return false;
            }
            rec.TurnsUsed++;
            remainingTurns = Math.Max(0, rec.Residency.MaxTrialTurns - rec.TurnsUsed);
            return true;
        }
    }

    public int GetTurnsUsed(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return 0; }
        lock (gate)
        {
            return records.TryGetValue(brainScopeId, out var rec) ? rec.TurnsUsed : 0;
        }
    }

    /// <summary>
    /// Atomically verifies that <paramref name="brainScopeId"/> is live and pins it against background sweeper / LRU eviction
    /// while <see cref="BrainClaimService.ClaimAsync"/> commits promotion into durable storage.
    /// Returns <see langword="false"/> if the brain is missing, expired, or already being claimed.
    /// </summary>
    public bool TryBeginClaim(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return false; }
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (!records.TryGetValue(brainScopeId, out var rec)
                || rec.Claiming
                || now - rec.LastActivityAt >= rec.Residency.IdleTtl)
            {
                return false;
            }

            rec.Claiming = true;
            rec.LastActivityAt = now;
            return true;
        }
    }

    /// <summary>
    /// Releases an in-flight claim pin on <paramref name="brainScopeId"/> if it has not already been promoted/removed.
    /// </summary>
    public void EndClaim(string brainScopeId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return; }
        lock (gate)
        {
            if (records.TryGetValue(brainScopeId, out var rec))
            {
                rec.Claiming = false;
            }
        }
    }

    public void MarkPromoted(string brainScopeId, string ownerPrincipalId)
    {
        if (string.IsNullOrWhiteSpace(brainScopeId)) { return; }
        lock (gate)
        {
            records.Remove(brainScopeId);
        }
        // Do NOT remove from AuthorityMirror here: IdentityDirectoryGrain.Commit already published the durable row.
    }

    /// <summary>
    /// Evicts idle-expired ephemeral brains and enforces both <see cref="EphemeralBrainOptions.MaxActiveBrains"/>
    /// and <see cref="EphemeralBrainOptions.TotalMemoryBudgetBytes"/> via LRU eviction.
    /// Brains pinned via <see cref="TryBeginClaim"/> are never evicted while promotion is in flight.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var now = clock.GetUtcNow();
        var backingStorage = Storage;
        List<string> evicted = [];

        lock (gate)
        {
            foreach (var (id, rec) in records.ToArray())
            {
                if (!rec.Claiming && now - rec.LastActivityAt >= rec.Residency.IdleTtl)
                {
                    records.Remove(id);
                    evicted.Add(id);
                }
            }

            var maxActive = Math.Max(1, opts.MaxActiveBrains);
            while (records.Count > maxActive)
            {
                var lru = records.Values.Where(r => !r.Claiming).OrderBy(r => r.LastActivityAt).FirstOrDefault();
                if (lru is null) { break; }
                records.Remove(lru.BrainId);
                evicted.Add(lru.BrainId);
            }

            if (backingStorage is not null && opts.TotalMemoryBudgetBytes > 0 && records.Count > 0)
            {
                var ordered = records.Values.OrderBy(r => r.LastActivityAt).ToList();
                long total = 0;
                Dictionary<string, long> sizes = new(StringComparer.Ordinal);
                foreach (var rec in ordered)
                {
                    var bytes = backingStorage.GetMemoryUsedBytes(rec.BrainId);
                    sizes[rec.BrainId] = bytes;
                    total += bytes;
                }

                foreach (var rec in ordered)
                {
                    if (total <= opts.TotalMemoryBudgetBytes) { break; }
                    if (rec.Claiming) { continue; }
                    records.Remove(rec.BrainId);
                    evicted.Add(rec.BrainId);
                    total -= sizes[rec.BrainId];
                }
            }
        }

        foreach (var id in evicted)
        {
            mirror?.Remove(id);
            if (backingStorage is not null)
            {
                await backingStorage.EvictAsync(id, ct).ConfigureAwait(false);
            }
        }

        return evicted.Count;
    }
}

internal sealed class EphemeralBrainRecord
{
    public string BrainId { get; }
    public BrainResidency.Ephemeral Residency { get; }
    public Account Account { get; }
    public Member Owner { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int TurnsUsed { get; set; }
    public bool Claiming { get; set; }
    public List<Grant> Grants { get; }

    public EphemeralBrainRecord(
        string brainScopeId,
        BrainResidency.Ephemeral residency,
        Account account,
        Member owner,
        DateTimeOffset createdAt,
        List<Grant> grants)
    {
        BrainId = brainScopeId;
        Residency = residency;
        Account = account;
        Owner = owner;
        CreatedAt = createdAt;
        LastActivityAt = createdAt;
        Grants = grants;
    }

    public IdentityBrainRow ToAuthorityRow() => new()
    {
        BrainId = BrainId,
        AccountId = Account.AccountId,
        Members = [Owner],
        Grants = [.. Grants],
        OwnerProvisioned = true,
        Kind = BrainKind.Personal,
    };
}

internal sealed class EphemeralBrainSweeper(
    EphemeralBrainRegistry registry,
    IOptions<EphemeralBrainOptions> options,
    ILogger<EphemeralBrainSweeper> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> EvictedBrains =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(EvictedBrains)), "Evicted {Count} expired or over-budget ephemeral brains from RAM.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero) { interval = TimeSpan.FromMinutes(1); }
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) { break; }
                var count = await registry.SweepAsync(stoppingToken).ConfigureAwait(false);
                if (count > 0) { EvictedBrains(logger, count, null); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Ephemeral brain sweep encountered an error.");
            }
        }
    }
}
