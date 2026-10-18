namespace IntoChat.OS;

/// <summary>
/// Marks a persisted state type as customer data that the portable brain export may copy.
/// Export is default-deny: an unmarked state type is never exported, and identity or secret
/// state names stay denied even when the type is marked.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ExportableStateAttribute : Attribute;
