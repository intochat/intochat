namespace IntoChat.OS.Framework.Modules;

/// <summary>
/// Declares a sealed per-brain secret field of an integration. Class-level only: a secret is never
/// an options member, so this attribute cannot decorate a property.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class SecretAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The integration id this secret belongs to; required when the module declares more than one integration.</summary>
    public string? Integration { get; init; }

    public bool Optional { get; init; }
}
