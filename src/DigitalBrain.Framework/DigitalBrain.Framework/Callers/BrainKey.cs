namespace DigitalBrain.Framework;

// Every brain-owned neuron key is "{brain}" or starts with "{brain}/".
public static class BrainKey
{
    public static string BrainOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var separator = key.IndexOf('/');
        return separator < 0 ? key : key[..separator];
    }

    public static bool BelongsTo(string key, string brain)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(brain);
        return key.Length == brain.Length
            ? string.Equals(key, brain, StringComparison.Ordinal)
            : key.Length > brain.Length && key[brain.Length] == '/' && key.StartsWith(brain, StringComparison.Ordinal);
    }

    // The brain a key belongs to for access checks. Keys compose it with '/' for path-shaped
    // neurons and ':' for UI-part names; either way the first segment is the brain. A key with no
    // separator, or whose first segment is not a brain id, names nothing brain-scoped.
    public static string? ScopeOf(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var separator = key.AsSpan().IndexOfAny('/', ':');
        if (separator <= 0) { return null; }
        var scope = key[..separator];
        return BrainScope.IsValidId(scope) ? scope : null;
    }
}
