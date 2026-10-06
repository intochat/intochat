using System.Diagnostics.CodeAnalysis;

namespace DigitalBrain.Framework.Enforcement;

/// <summary>
/// The brain a call is in. The brain's id is its kernel's grain key and the prefix of every
/// neuron key inside it; there is no second, derived id.
/// </summary>
public sealed record BrainScope(string Id, string Owner, string Name)
{
    public static string CurrentId()
    {
        if (!CallerContextStamper.TryGet(out var caller))
        { throw new InvalidOperationException("No caller context is stamped on this call."); }
        return Create(caller.AccountId, caller.BrainId).Id;
    }

    public static BrainScope Create(string owner, string name)
    {
        if (!IsValidId(name))
        { throw new ArgumentException("A brain ID must contain 1–200 characters without path separators.", nameof(name)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return new(name, owner, name);
    }

    // Brain, thread and run ids travel in routes and grain keys: 1-200 characters, no control
    // characters and no path separators.
    public static bool IsValidId([NotNullWhen(true)] string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(c => char.IsControl(c) || c is '/' or '\\');
}
