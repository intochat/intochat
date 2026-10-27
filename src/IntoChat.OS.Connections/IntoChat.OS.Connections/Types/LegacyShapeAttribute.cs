using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

// Marks a persisted member kept only so older stored bytes still deserialise. It keeps its original Id; the type
// converts it to the current array shape on read and never writes it again. Exempt from the persisted-collection rule.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class LegacyShapeAttribute : Attribute;
