namespace IntoChat.Aspire.Domain;

internal abstract record TargetResolution;

internal sealed record ResolvedTarget(string ModelName, AspireInstanceView Instance) : TargetResolution;

internal sealed record AmbiguousTarget(string ModelName, string[] ReplicaIds) : TargetResolution;

internal sealed record UnknownTarget(string[] KnownModelNames) : TargetResolution;

// Immutable host-level topology projection: resolves a caller's target (instance id or unique model name)
// without fanning out across replicas.
internal sealed class RuntimeTopology(bool connected, AspireResource[] items)
{
    public const int MaxKnown = 64;

    public bool Connected { get; } = connected;
    public AspireResource[] Items { get; } = items ?? [];

    public static RuntimeTopology Disconnected { get; } = new(false, []);

    public static RuntimeTopology From(AspireResources resources)
        => new(resources.Connected, resources.Items);

    public AspireResources ToContract() => new(Connected, Items);

    public TargetResolution Resolve(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new UnknownTarget(ModelNames());
        }

        var trimmed = target.Trim();

        // 1. Exact instance id match always wins.
        foreach (var resource in Items)
        {
            foreach (var instance in resource.Instances)
            {
                if (string.Equals(instance.Id, trimmed, StringComparison.Ordinal))
                {
                    return new ResolvedTarget(resource.Name, instance);
                }
            }
        }

        // 2. Exact model name match: unambiguous only when the model has exactly one instance.
        var model = Items.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.Ordinal));
        if (model is not null)
        {
            return model.Instances switch
            {
                [var only] => new ResolvedTarget(model.Name, only),
                { Length: > 1 } replicas => new AmbiguousTarget(model.Name, [.. replicas.Select(i => i.Id).Take(MaxKnown)]),
                _ => new UnknownTarget(ModelNames()),
            };
        }

        return new UnknownTarget(ModelNames());
    }

    private string[] ModelNames()
        => [.. Items.Select(r => r.Name).Take(MaxKnown)];
}
