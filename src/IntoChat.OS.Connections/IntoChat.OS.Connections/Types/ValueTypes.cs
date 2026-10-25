using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections;

public readonly record struct Reference(string EntityId, string? Label = null);
