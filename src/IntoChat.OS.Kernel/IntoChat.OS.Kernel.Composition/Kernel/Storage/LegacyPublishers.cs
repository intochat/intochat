using IntoChat.OS.Framework;

namespace IntoChat.OS.Kernel.Composition;

// Transitional (06-02, deleted in 06-08): where a legacy grain's journal lives and the publisher name it writes under.
internal static class LegacyPublishers
{
    public static string BrainOf(GrainId grainId) => BrainKey.BrainOf(grainId.Key.ToString()!);

    // A grain keyed "{brain}/{rest}" publishes as the DigitalBrain neuron id "{brain}/{type}/{rest}", so a plain [On] subscriber
    // names it like any neuron; other keys (the brain itself, platform scopes) keep the grain id, which no plain neuron names.
    public static string Of(GrainId grainId)
    {
        var key = grainId.Key.ToString()!;
        var brain = BrainKey.BrainOf(key);
        if (key.Length > brain.Length + 1)
        {
            var candidate = $"{brain}/{grainId.Type}/{key[(brain.Length + 1)..]}";
            if (DigitalBrain.NeuronId.TryParse(candidate, out _)) { return candidate; }
        }
        return grainId.ToString();
    }
}
