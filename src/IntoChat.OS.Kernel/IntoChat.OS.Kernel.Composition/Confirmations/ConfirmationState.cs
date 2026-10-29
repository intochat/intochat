using DigitalBrain.Confirmation;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

internal sealed record ConfirmationEntry(ConfirmationRequested Request, ConfirmationResult? Result = null);

internal sealed record ConfirmationState
{
    public long Revision { get; init; }
    public ConfirmationEntry[] Entries { get; init; } = [];
}
