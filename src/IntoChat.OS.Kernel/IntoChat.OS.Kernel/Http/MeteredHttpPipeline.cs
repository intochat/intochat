using DigitalBrain;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Http;

/// <summary>
/// Registers named, rate-limited <see cref="HttpClient"/> pipelines declared via <see cref="Need.HttpClient"/>
/// on each active <see cref="ModuleManifest"/>.
/// </summary>
public static class MeteredHttpPipeline
{
    public static IServiceCollection AddMeteredHttpPipelines(
        this IServiceCollection services,
        IEnumerable<OsModule> modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(modules);

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module.Type == typeof(OsModule)) { continue; }
            var manifest = ModuleManifest.Of(module.Type);
            foreach (var need in manifest.Needs)
            {
                if (need.Kind != NeedKind.Http || string.IsNullOrWhiteSpace(need.Name) || need.TargetNeuronType is not null)
                {
                    continue;
                }

                if (!registered.Add(need.Name)) { continue; }
                var baseAddressKey = need.BaseAddressSetting;
                var rpm = need.RequestsPerMinute ?? 120;
                services.AddHttpClient(need.Name, (provider, client) =>
                {
                    if (!string.IsNullOrWhiteSpace(baseAddressKey)
                        && provider.GetService<IConfiguration>()?[baseAddressKey] is { Length: > 0 } rawUri
                        && Uri.TryCreate(rawUri, UriKind.Absolute, out var uri))
                    {
                        client.BaseAddress = uri;
                    }
                    client.DefaultRequestHeaders.TryAddWithoutValidation("X-IntoChat-RateLimit-RPM", rpm.ToString(System.Globalization.CultureInfo.InvariantCulture));
                });
            }
        }

        return services;
    }
}
