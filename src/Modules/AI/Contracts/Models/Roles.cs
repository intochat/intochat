using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Models;

[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    Chat,
    Think,
    Fast,
    Hear,
    Speak,
    Talk,
    Embed,
    Image,
}

public sealed record RoleMap(
    ImmutableDictionary<Role, ModelId> Device,
    ImmutableDictionary<Role, ModelId> Cloud)
{
    public static RoleMap Empty { get; } = new(
        ImmutableDictionary<Role, ModelId>.Empty,
        ImmutableDictionary<Role, ModelId>.Empty);
}

public static class RoleNeeds
{
    public static Abilities Of(Role role) => role switch
    {
        Role.Chat or Role.Think or Role.Fast => Abilities.Text,
        Role.Hear => Abilities.Hear,
        Role.Speak => Abilities.Speak,
        Role.Talk => Abilities.Talk,
        Role.Embed => Abilities.Embed,
        Role.Image => Abilities.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown model role."),
    };
}
