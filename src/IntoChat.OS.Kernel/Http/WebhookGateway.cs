using DigitalBrain;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Kernel.Http;

/// <summary>
/// Inspects declarative <see cref="Need.Webhook"/> entries across active <see cref="ModuleManifest"/> declarations
/// so modules can declare their inbound webhook paths and target neuron types in one manifest.
/// </summary>
public sealed record WebhookRoute(string ModuleId, string Path, string TargetNeuronType);

public static class WebhookGateway
{
    public static IReadOnlyList<WebhookRoute> DiscoverRoutes(IEnumerable<OsModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var routes = new List<WebhookRoute>();
        foreach (var module in modules)
        {
            if (module.Type == typeof(OsModule)) { continue; }
            var manifest = ModuleManifest.Of(module.Type);
            foreach (var need in manifest.Needs)
            {
                if (need.Kind == NeedKind.Http && !string.IsNullOrWhiteSpace(need.Name) && !string.IsNullOrWhiteSpace(need.TargetNeuronType))
                {
                    routes.Add(new WebhookRoute(module.Id.Value, need.Name, need.TargetNeuronType));
                }
            }
        }
        return routes;
    }
}
