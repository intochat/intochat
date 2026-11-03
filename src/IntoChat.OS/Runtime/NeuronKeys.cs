using DigitalBrain;

namespace IntoChat.OS;

public static class NeuronKeys
{
    public static string FullKey(this NeuronId id) => $"{id.Brain.Value}/{id.Key}";
}
