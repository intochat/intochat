using System.Collections.Immutable;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.AI.Models;

internal static class RoleMaps
{
    internal static RoleMap For(AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Roles.ToRoleMap();
        var cloudOnlyConfigured = configured.Device.IsEmpty && !configured.Cloud.IsEmpty;

        var device = cloudOnlyConfigured
            ? ImmutableDictionary.CreateBuilder<ModelRole, ModelId>()
            : KnownModels.Defaults.Device.ToBuilder();
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

    internal static RoleMap DefaultsFor(AIOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Roles.ToRoleMap();
        return configured.Device.IsEmpty && !configured.Cloud.IsEmpty
            ? new RoleMap(ImmutableDictionary<ModelRole, ModelId>.Empty, KnownModels.Defaults.Cloud)
            : KnownModels.Defaults;
    }

    internal static IEnumerable<ModelId> Deployment(RoleMap map, string? topology, ModelRole role)
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
                if (map.Device.TryGetValue(role, out var deviceOnly))
                {
                    yield return deviceOnly;
                }
                else if (map.Device.IsEmpty && map.Cloud.TryGetValue(role, out var cloudFallback))
                {
                    yield return cloudFallback;
                }
                break;
        }
    }

    // When no topology is set in configuration, try Device first and then Cloud so tests or hosts
    // configuring only Roles.Cloud (or legacy Default:*) still resolve without setting Topology.
    internal static IEnumerable<ModelId> Candidates(RoleMap map, string? topology, ModelRole role)
    {
        if (string.IsNullOrWhiteSpace(topology))
        {
            var seen = default(ModelId?);
            if (map.Device.TryGetValue(role, out var device))
            {
                seen = device;
                yield return device;
            }
            if (map.Cloud.TryGetValue(role, out var cloud) && cloud != seen)
            {
                yield return cloud;
            }
            yield break;
        }

        foreach (var id in Deployment(map, topology, role))
        {
            yield return id;
        }
    }

    // Returns only explicitly configured role ids (not KnownModels.Defaults), useful for opt-in features
    // where an unconfigured role must remain off.
    internal static ModelId? Explicit(AIOptions options, string? topology, ModelRole role)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Roles.ToRoleMap();
        foreach (var id in Candidates(configured, topology, role))
        {
            return id;
        }

        return null;
    }
}
