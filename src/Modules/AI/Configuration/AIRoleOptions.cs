using System.Collections.Immutable;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.AI;

public sealed class AIRoleOptions
{
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<Role, string> Device { get; } = [];

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Dictionary<Role, string> Cloud { get; } = [];

    public RoleMap ToRoleMap()
    {
        var device = ImmutableDictionary.CreateBuilder<Role, ModelId>();
        foreach (var (role, raw) in Device)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            device[role] = KnownModels.Find(raw)?.Id ?? new ModelId(raw.Trim());
        }

        var cloud = ImmutableDictionary.CreateBuilder<Role, ModelId>();
        foreach (var (role, raw) in Cloud)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            cloud[role] = KnownModels.Find(raw)?.Id ?? new ModelId(raw.Trim());
        }

        return new RoleMap(device.ToImmutable(), cloud.ToImmutable());
    }
}
