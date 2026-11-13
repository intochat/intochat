using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Work;

[JsonConverter(typeof(JsonStringEnumConverter<ApprovalStatus>))]
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Withdrawn,
}

/// <summary>
/// Domain resolution record for a requested approval plan.
/// </summary>
public sealed record ApprovalResolution(
    string PlanId,
    ApprovalStatus Status,
    DateTimeOffset ResolvedAt,
    string ResolvedBy,
    string? Reason = null);

/// <summary>
/// In-memory idempotent approval tracker that prevents double resolution.
/// </summary>
public sealed class WorkApprovalTracker
{
    private readonly ConcurrentDictionary<string, ApprovalResolution> _resolutions = new(StringComparer.Ordinal);

    public bool IsResolved(string planId) => _resolutions.ContainsKey(planId);

    public bool TryGetResolution(string planId, out ApprovalResolution? resolution)
    {
        if (_resolutions.TryGetValue(planId, out var existing))
        {
            resolution = existing;
            return true;
        }
        resolution = null;
        return false;
    }

    /// <summary>
    /// Attempts to resolve a plan with the given status.
    /// If the plan was already resolved, returns false and outputs the existing resolution to enforce idempotency.
    /// </summary>
    public bool TryResolve(string planId, ApprovalStatus status, string resolvedBy, DateTimeOffset resolvedAt, string? reason, out ApprovalResolution resolution)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedBy);

        var candidate = new ApprovalResolution(planId, status, resolvedAt, resolvedBy, reason);
        if (_resolutions.TryAdd(planId, candidate))
        {
            resolution = candidate;
            return true;
        }

        resolution = _resolutions[planId];
        return false;
    }

    public IReadOnlyList<ApprovalResolution> AllResolutions()
        => _resolutions.Values.OrderBy(r => r.ResolvedAt).ToArray();
}
