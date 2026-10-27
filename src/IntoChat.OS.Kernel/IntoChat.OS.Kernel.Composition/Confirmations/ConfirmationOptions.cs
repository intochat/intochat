using DigitalBrain.Confirmation;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

// Host-owned policy, scoped by brain. App settings cannot change this policy.
public sealed class ConfirmationOptions
{
    public const string Section = "IntoChatOS:Confirmations";
    public Dictionary<string, ConfirmationPolicy> Brains { get; set; } = new(StringComparer.Ordinal);

    public ConfirmationPolicy For(string brain)
        => Brains.TryGetValue(brain, out var policy) && Enum.IsDefined(policy) ? policy : ConfirmationPolicy.DestructiveOnly;
}
