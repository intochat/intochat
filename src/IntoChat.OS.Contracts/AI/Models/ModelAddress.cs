namespace IntoChat.OS.Modules.AI.Models;

// Turns a brain id (and optional model id) into its ILLM neuron address.
// ILLM serves every caller as its own brain, but its key still belongs to one brain like every neuron key:
// the inference signals it publishes journal into that brain, and a machine refuses a key outside every brain.
public static class ModelAddress
{
    // A brain's role-default model neuron (resolves ModelRole.Chat for that brain).
    public static string Key(string brainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        if (brainId.Contains('/')) { throw new ArgumentException($"'{brainId}' is a neuron key, not a brain id.", nameof(brainId)); }
        return $"{brainId}/models";
    }

    public static bool IsKnown(string model) => KnownModels.Find(model) is not null;
}
