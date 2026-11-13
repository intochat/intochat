using DigitalBrain;

namespace IntoChat.Cloud.Auth.Services;

// The neuron id of an Auth key: the contracts name their neurons by the os full key "{brain}/{key}"; the runtime id is
// brain, neuron type, key.
internal static class AuthIds
{
    public static NeuronId Of(string type, string fullKey)
    {
        var slash = fullKey.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0) { throw new ArgumentException($"'{fullKey}' names no brain: a neuron key is {{brain}}/{{key}}.", nameof(fullKey)); }
        return new NeuronId(new BrainId(fullKey[..slash]), type, fullKey[(slash + 1)..]);
    }
}
