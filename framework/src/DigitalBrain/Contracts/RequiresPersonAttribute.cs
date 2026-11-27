namespace DigitalBrain.Contracts;

/// <summary>
/// Marks a neuron contract interface or method as requiring a direct signed-in human person caller
/// (<c>RequiresPerson = true</c>); agents and apps cannot invoke or self-approve it.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, Inherited = false)]
public sealed class RequiresPersonAttribute(string? reason = null) : Attribute
{
    /// <summary>Optional human-readable explanation surfaced in confirmation prompts.</summary>
    public string? Reason { get; } = reason;
}
