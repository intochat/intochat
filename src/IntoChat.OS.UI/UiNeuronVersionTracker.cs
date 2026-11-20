namespace IntoChat.OS.UI;

/// <summary>
/// Tracks the highest observed version per interactive <c>Modules.UI</c> neuron key so stale
/// out-of-order signals never clobber newer screen state.
/// </summary>
public sealed class UiNeuronVersionTracker
{
    private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

    /// <summary>Returns <c>true</c> if <paramref name="version"/> is older than or equal to the last recorded version for <paramref name="key"/>.</summary>
    public bool IsStale(string key, int version)
        => _versions.TryGetValue(key, out var last) && version <= last;

    /// <summary>Records <paramref name="version"/> if it is strictly newer than the last recorded version for <paramref name="key"/>.</summary>
    public void Record(string key, int version)
    {
        if (!_versions.TryGetValue(key, out var last) || version > last)
        {
            _versions[key] = version;
        }
    }

    /// <summary>Returns <c>true</c> and advances the tracked version when <paramref name="version"/> is strictly newer than the last recorded version.</summary>
    public bool TryAccept(string key, int version)
    {
        if (IsStale(key, version))
        {
            return false;
        }

        _versions[key] = version;
        return true;
    }
}
