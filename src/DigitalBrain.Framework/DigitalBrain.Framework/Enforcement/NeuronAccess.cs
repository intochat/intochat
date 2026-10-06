namespace DigitalBrain.Framework.Enforcement;

// Supplied by the receiving module, never by a caller or a discovery result.
public sealed record NeuronAccess(string? Scope = null, bool Public = false)
{
    public static NeuronAccess Unclassified { get; } = new();
    public static NeuronAccess PublicOperation { get; } = new(Public: true);

    // A brain id is an address-level tenant boundary. Keys compose it with '/' for path-shaped
    // neurons and ':' for UI-part names; either way the first segment is the brain. Modules with
    // aliases, persisted owners or public operations override it.
    public static NeuronAccess ForBrainKey(string key)
    {
        if (key.StartsWith("connection/", StringComparison.Ordinal))
        {
            try { key = DigitalBrain.Framework.Platform.Integrations.ConnectionKeys.Parse(key).BrainId; }
            catch (ArgumentException) { return Unclassified; }
        }
        var separator = key.AsSpan().IndexOfAny('/', ':');
        var scope = separator < 0 ? key : key[..separator];
        return IsBrainId(scope) ? new(scope) : Unclassified;
    }

    public static bool IsBrainId(string value)
        => value.Length >= 42 && value.StartsWith("workspace-", StringComparison.Ordinal)
            && !value.AsSpan(10).ContainsAnyExcept("0123456789abcdef");
}
