using System.ComponentModel;

namespace IntoChat.OS.Connections;

// Preserved for binary compatibility with external module packages (e.g. IntoChat.OS.Modules.Microsoft.CSharp).
// In-tree state models no longer use this attribute.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LegacyShapeAttribute : Attribute;
