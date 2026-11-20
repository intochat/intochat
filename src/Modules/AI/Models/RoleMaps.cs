using System.Collections.Immutable;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Models;

internal static class RoleMaps
{
    internal static RoleMap For(AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Roles.ToRoleMap();
        var device = KnownModels.Defaults.Device.ToBuilder();
        foreach (var (role, id) in configured.Device)
        {
            device[role] = id;
        }

        var cloud = KnownModels.Defaults.Cloud.ToBuilder();
        foreach (var (role, id) in configured.Cloud)
        {
            cloud[role] = id;
        }

        return new RoleMap(device.ToImmutable(), cloud.ToImmutable());
    }

    internal static IEnumerable<ModelId> Deployment(RoleMap map, string? topology, Role role)
    {
        ArgumentNullException.ThrowIfNull(map);
        var canonical = IntoChatOSNames.CanonicalTopology(topology);
        switch (canonical)
        {
            case IntoChatOSNames.TopologyCloud:
                if (map.Cloud.TryGetValue(role, out var cloudOnly)) { yield return cloudOnly; }
                break;

            case IntoChatOSNames.TopologyLocalCloud:
                if (map.Device.TryGetValue(role, out var first)) { yield return first; }
                if (map.Cloud.TryGetValue(role, out var second) && second != first) { yield return second; }
                break;

            default:
                if (map.Device.TryGetValue(role, out var deviceOnly)) { yield return deviceOnly; }
                break;
        }
    }

    // Returns only explicitly configured role ids (not KnownModels.Defaults), useful for opt-in features
    // like VoiceToTextHosting where an unconfigured role must remain off (503).
    internal static ModelId? Explicit(AIOptions options, string? topology, Role role)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Roles.ToRoleMap();
        foreach (var id in Deployment(configured, topology, role))
        {
            return id;
        }

        if (string.IsNullOrWhiteSpace(topology))
        {
            if (configured.Cloud.TryGetValue(role, out var cloud)) { return cloud; }
        }

        return null;
    }
}
