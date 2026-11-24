namespace IntoChat.OS.Modules.Apps;

/// <summary>Outcome status of a slot-binding request on <see cref="IApp.BindSlots"/>.</summary>
public enum SlotBindingStatus
{
    Ok = 0,
    PendingConfirmation = 1,
    Refused = 2,
}

/// <summary>Result returned by <see cref="IApp.BindSlots"/>.</summary>
public sealed record SlotBindingOutcome(
    SlotBindingStatus Status,
    string? ConfirmationId = null,
    string? Reason = null,
    AppSnapshot? App = null)
{
    public static SlotBindingOutcome Bound(AppSnapshot app)
        => new(SlotBindingStatus.Ok, App: app);

    public static SlotBindingOutcome Pending(string confirmationId, string? reason = null)
        => new(SlotBindingStatus.PendingConfirmation, ConfirmationId: confirmationId, Reason: reason);

    public static SlotBindingOutcome Refused(string reason)
        => new(SlotBindingStatus.Refused, Reason: reason);
}
