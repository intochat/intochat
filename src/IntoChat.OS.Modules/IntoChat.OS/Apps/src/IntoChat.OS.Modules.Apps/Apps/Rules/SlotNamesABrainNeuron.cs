using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Rules;

internal sealed record SlotNamesABrainNeuron(string Slot, string Brain) : Rule("apps.presentation-slot")
{
    public override bool Broken => string.IsNullOrWhiteSpace(Slot) || Slot.Length > 512
        || !Slot.StartsWith(Brain + "/", StringComparison.Ordinal)
        || Slot.Split('/').Any(part => part.Length == 0 || part is "." or "..");
    public override string Message => "A slot names a neuron of this brain.";
}
