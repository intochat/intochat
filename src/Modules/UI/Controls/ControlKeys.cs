using DigitalBrain;

namespace IntoChat.OS.Modules.UI.Controls;

/// <summary>The key a control neuron draws its view under: the last segment of its address (a child's key).</summary>
internal static class ControlKeys
{
    internal static string Of(NeuronId id)
    {
        var slash = id.Key.LastIndexOf('/');
        return slash < 0 ? id.Key : id.Key[(slash + 1)..];
    }
}
