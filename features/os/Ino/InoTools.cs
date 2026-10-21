using IntoChat.OS;

// Optional host modules may be disabled. Keep the app's allowlist, but request only
// neuron contracts this host actually exposes; never add other discovered tools.
public static class InoTools
{
    public static string[] Select(string[] allowed, TypeEntry[] available)
    {
        ArgumentNullException.ThrowIfNull(available);
        var neurons = available.Where(type => type.Kind == TypeKind.Neuron)
            .Select(type => type.Name).ToHashSet(StringComparer.Ordinal);
        return [.. allowed.Where(neurons.Contains)];
    }
}
