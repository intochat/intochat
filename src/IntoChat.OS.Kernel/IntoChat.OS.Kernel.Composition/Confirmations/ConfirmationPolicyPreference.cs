using DigitalBrain.Confirmation;

namespace IntoChat.OS.Kernel.Composition.Confirmations;

/// <summary>
/// The per-brain confirmation policy (07 D-22). Default <see cref="ConfirmationPolicy.DestructiveOnly"/>.
/// The settings row is <see cref="Section"/>; the host binds the same section as <see cref="ConfirmationOptions"/>.
/// </summary>
public sealed class ConfirmationPolicyPreference
{
    public const string Section = ConfirmationOptions.Section;

    public ConfirmationPolicy Value { get; set; } = ConfirmationPolicy.DestructiveOnly;

    public Dictionary<string, ConfirmationPolicy> Brains { get; set; } = new(StringComparer.Ordinal);

    public ConfirmationPolicy For(string brain)
        => Brains.TryGetValue(brain, out var policy) && Enum.IsDefined(policy) ? policy : Value;

    public static ConfirmationPolicy For(ConfirmationOptions options, string brain)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.For(brain);
    }
}
