using DigitalBrain.Confirmation;

namespace IntoChat.OS.Modules.Flutter.Views;

// The one wire shape of a confirmation answer, Resolve:{escaped plan id}:{true|false}: written by the inbox surface and the
// presenters' inert approval buttons, read back by the edge.
public static class ConfirmationActions
{
    public static string Format(string planId, bool approve)
        => IConfirmations.ResolveOperation + ":" + Uri.EscapeDataString(planId) + ":" + (approve ? "true" : "false");

    public static (string PlanId, bool Approve) Answer(string? action)
    {
        var parts = action?.Split(':');
        if (parts is not [IConfirmations.ResolveOperation, { Length: > 0 } id, "true" or "false"])
        { throw new ArgumentException("Invalid confirmation answer."); }
        var decoded = Uri.UnescapeDataString(id);
        if (decoded.Length > 128 || Uri.EscapeDataString(decoded) != id)
        { throw new ArgumentException("Invalid confirmation plan id."); }
        return (decoded, parts[2] == "true");
    }
}
