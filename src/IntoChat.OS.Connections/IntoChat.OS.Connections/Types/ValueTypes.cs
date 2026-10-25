using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Connections;

public readonly record struct Reference(string EntityId, string? Label = null);
