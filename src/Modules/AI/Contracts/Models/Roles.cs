using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.AI.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ModelRole>))]
public enum ModelRole
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
    ImmutableDictionary<ModelRole, ModelId> Device,
    ImmutableDictionary<ModelRole, ModelId> Cloud)
{
    public static RoleMap Empty { get; } = new(
        ImmutableDictionary<ModelRole, ModelId>.Empty,
        ImmutableDictionary<ModelRole, ModelId>.Empty);
}

public static class RoleNeeds
{
    public static Abilities Of(ModelRole role) => role switch
    {
        ModelRole.Chat or ModelRole.Think or ModelRole.Fast => Abilities.Text,
        ModelRole.Hear => Abilities.Hear,
        ModelRole.Speak => Abilities.Speak,
        ModelRole.Talk => Abilities.Talk,
        ModelRole.Embed => Abilities.Embed,
        ModelRole.Image => Abilities.Image,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown model role."),
    };
}
