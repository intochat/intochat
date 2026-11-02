// os-only: os ingress trust configuration under IntoChatOS:ForwardedHeaders.
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework.Modules.Http;

// The ingress is trusted only when an operator has named its proxy or network. Empty lists ignore forwarding.
// Keys under the section: ForwardLimit (1-5), KnownProxies[], KnownNetworks[] (CIDR), AllowedHosts[] (exact host names).
public sealed class ForwardedHeadersSetup(IConfiguration configuration, string section = ForwardedHeadersSetup.DefaultSection) : IStartupFilter
{
    public const string DefaultSection = "IntoChatOS:ForwardedHeaders";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseForwardedHeaders(CreateOptions(configuration, section));
        next(app);
    };

    public static ForwardedHeadersOptions CreateOptions(IConfiguration configuration, string section = DefaultSection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new ForwardedHeadersOptions { ForwardLimit = 1 };
        if (configuration[section + ":ForwardLimit"] is { } configuredLimit)
        {
            if (!int.TryParse(configuredLimit, out var limit) || limit is < 1 or > 5)
            {
                throw new InvalidOperationException($"{section}:ForwardLimit must be between 1 and 5.");
            }
            options.ForwardLimit = limit;
        }
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var item in configuration.GetSection(section + ":KnownProxies").GetChildren())
        {
            if (!IPAddress.TryParse(item.Value, out var address)) { throw new InvalidOperationException($"Invalid {section}:KnownProxies entry."); }
            options.KnownProxies.Add(address);
        }
        foreach (var item in configuration.GetSection(section + ":KnownNetworks").GetChildren())
        {
            if (!System.Net.IPNetwork.TryParse(item.Value, out var network)) { throw new InvalidOperationException($"Invalid {section}:KnownNetworks entry."); }
            options.KnownIPNetworks.Add(network);
        }
        foreach (var item in configuration.GetSection(section + ":AllowedHosts").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(item.Value) || Uri.CheckHostName(item.Value) == UriHostNameType.Unknown || item.Value.Contains('*'))
            {
                throw new InvalidOperationException($"{section}:AllowedHosts entries must be explicit host names.");
            }
            options.AllowedHosts.Add(item.Value);
        }
        if (options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // A front proxy may route the upstream Host to the backend name. Restore a public Host only when both the
            // ingress address and the exact host name are pinned; an empty allowlist never enables forwarded Host.
            if (options.AllowedHosts.Count > 0) { options.ForwardedHeaders |= ForwardedHeaders.XForwardedHost; }
        }
        return options;
    }
}

public static class ForwardedHeadersHosting
{
    // Applies forwarded headers first in the pipeline, from the named configuration section.
    public static IServiceCollection AddForwardedHeadersSetup(this IServiceCollection services, string section = ForwardedHeadersSetup.DefaultSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<IStartupFilter>(provider => new ForwardedHeadersSetup(provider.GetRequiredService<IConfiguration>(), section));
    }
}
