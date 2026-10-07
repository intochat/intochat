namespace DigitalBrain.Framework.Enforcement;

// Supplied by the receiving module, never by a caller or a discovery result.
public sealed record NeuronAccess(string? Scope = null, bool Public = false)
{
    public static NeuronAccess Unclassified { get; } = new();
    public static NeuronAccess PublicOperation { get; } = new(Public: true);

    // A brain id is an address-level tenant boundary. Keys compose it with '/' for path-shaped
    // neurons and ':' for UI-part names; either way the first segment is the brain. A key with no
    // separator names nothing brain-scoped. Modules with aliases, persisted owners or public
    // operations override it.
    public static NeuronAccess ForBrainKey(string key)
    {
        if (key.StartsWith("connection/", StringComparison.Ordinal))
        {
            try { return new(DigitalBrain.Framework.Platform.Integrations.ConnectionKeys.Parse(key).BrainId); }
            catch (ArgumentException) { return Unclassified; }
        }
        var separator = key.AsSpan().IndexOfAny('/', ':');
        return separator > 0 && BrainScope.IsValidId(key[..separator]) ? new(key[..separator]) : Unclassified;
    }
}
