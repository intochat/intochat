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
}
