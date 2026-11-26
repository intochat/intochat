using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Devices;

// Explicit, parsed reference to a model hosted on one of the owner's enrolled devices:
// "device:{name}/{modelId}", where {name} is 1-64 characters of [A-Za-z0-9._-].
public readonly record struct DeviceModelRef(string Device, ModelId Model)
{
    public const string Prefix = "device:";
    private const int MaxDeviceNameLength = 64;
    private const int MaxModelIdLength = 200;

    public ModelId ToModelId() => new($"{Prefix}{Device}/{Model.Value}");

    public static bool TryParse(ModelId id, out DeviceModelRef model)
        => TryParse(id.Value, out model);

    public static bool TryParse(string? raw, out DeviceModelRef model)
    {
        model = default;
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = raw.AsSpan(Prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash < 1 || slash > MaxDeviceNameLength || slash >= rest.Length - 1)
        {
            return false;
        }

        var device = rest[..slash];
        foreach (var ch in device)
        {
            if (!IsDeviceChar(ch))
            {
                return false;
            }
        }

        var modelPart = rest[(slash + 1)..];
        if (modelPart.Length == 0 || modelPart.Length > MaxModelIdLength)
        {
            return false;
        }

        foreach (var ch in modelPart)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        model = new DeviceModelRef(device.ToString(), new ModelId(modelPart.ToString()));
        return true;
    }

    private static bool IsDeviceChar(char ch)
        => (ch >= 'A' && ch <= 'Z')
            || (ch >= 'a' && ch <= 'z')
            || (ch >= '0' && ch <= '9')
            || ch is '.' or '_' or '-';
}
