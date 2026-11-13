namespace IntoChat.OS.Primitives;

/// <summary>
/// Canonical address for an open or addressable screen within a brain.
/// </summary>
[GenerateSerializer, Alias("os.screen-address")]
public readonly record struct ScreenAddress(
    [property: Id(0)] string BrainId,
    [property: Id(1)] string AppId,
    [property: Id(2)] string InstanceId = "main")
{
    public const string DefaultInstanceId = "main";

    public string Key => $"{BrainId}/{AppId}/{InstanceId}";

    public static bool TryParse(string? key, out ScreenAddress address)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            address = default;
            return false;
        }

        var parts = key.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1]))
        {
            address = default;
            return false;
        }

        var instanceId = parts.Length == 3 && !string.IsNullOrWhiteSpace(parts[2])
            ? parts[2]
            : DefaultInstanceId;

        address = new ScreenAddress(parts[0], parts[1], instanceId);
        return true;
    }

    public static ScreenAddress Parse(string key)
        => TryParse(key, out var address)
            ? address
            : throw new ArgumentException($"Invalid screen address '{key}'. Expected '{{brainId}}/{{appId}}[/{{instanceId}}]'.", nameof(key));

    public override string ToString() => Key;
}
