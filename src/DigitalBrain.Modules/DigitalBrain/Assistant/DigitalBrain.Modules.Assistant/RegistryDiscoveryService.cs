using DigitalBrain.Framework;

namespace DigitalBrain.Assistant;

internal sealed class RegistryDiscoveryService(IEnumerable<IRegistryResourceProvider> providers, IGrainFactory? grains = null)
{
    // The operating system's own apps (the assistant) discover on the person's behalf; an app
    // from any other publisher, or an OS app whose program the person edited, does not.
    private const string OperatingSystemPublisher = "os";

    public Task<RegistryDiscovery> Discover(string query, CancellationToken ct) => Browse(query, null, 0, 10, ct);

    private async Task RequireCaller()
    {
        var caller = CallerContextStamper.Require();
        if (!CallerContextStamper.IsTrusted(caller) || (caller.Kind == CallerKind.App && !await IsOperatingSystemApp(caller.AppId)))
        { throw new UnauthorizedAccessException("Only a trusted host may discover capabilities."); }
    }

    private async Task<bool> IsOperatingSystemApp(string? appKey)
    {
        if (grains is null || string.IsNullOrEmpty(appKey) || !appKey.StartsWith(BrainScope.CurrentId() + "/apps/", StringComparison.Ordinal)) { return false; }
        var app = await grains.GetGrain<DigitalBrain.Apps.IApp>(appKey).Read();
        return app.Origin is { Publisher: OperatingSystemPublisher } && !app.Edited;
    }

    public async Task<RegistryDiscovery> Browse(string query, string? provider, int offset, int limit, CancellationToken ct)
    {
        await RequireCaller();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 20);
        ct.ThrowIfCancellationRequested();
        if (provider is null)
        {
            // Rank module descriptions. Resource enumeration follows an explicit provider choice.
            var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var summaries = providers.Select(item => item.Summary)
                .OrderByDescending(item => terms.Count(term => (item.Id + " " + item.Description).Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(item => item.Id, StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray();
            return new([.. summaries.Take(limit).Select(Describe)], [], summaries.Length > limit ? offset + limit : null);
        }
        var source = providers.SingleOrDefault(item => item.Id == provider);
        if (source is null) { return new([], [new(provider, "This provider is not available.")]); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var result = await source.Browse(offset, limit, budget.Token).WaitAsync(budget.Token);
            return result with { Capabilities = result.Capabilities.Take(limit).Select(Describe).ToArray() };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new([], [new(provider, "Capability resources are temporarily unavailable.")]); }
    }

    public async Task<RegistryCapability?> Select(string id, CancellationToken ct)
    {
        await RequireCaller();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 512) { throw new ArgumentException("Capability IDs must be bounded.", nameof(id)); }
        var provider = id.Split(':', 2)[0];
        var source = providers.SingleOrDefault(item => item.Id == provider);
        if (source is null) { return null; }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        return await source.Select(id, budget.Token).WaitAsync(budget.Token);
    }

    private static RegistryCapability Describe(RegistryCapability capability) => capability with { Tools = [], ResourcesJson = null };
}
