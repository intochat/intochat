using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.ServiceDefaults;

// Open posture resolves every anonymous caller as the owner, so in Aspire run mode on the host it may listen on
// loopback only. Publish and container modes are out of scope: they already require an explicit posture
// (IdentityHosting), and the isolated candidate containers opt in explicitly.
//
// Two layers: Validate reads the URLs Kestrel will read from configuration and throws before anything binds;
// the backstop inspects the addresses the server actually bound (code-level UseUrls/ListenAnyIP are invisible
// to configuration) and stops the host.
public static class OpenPostureLoopback
{
    public const string PostureKey = "IntoChatOS:Auth:Posture";
    public const string AllowOpenOnNetworkKey = "IntoChatOS:Auth:AllowOpenOnNetwork";

    // Run-mode marker the AppHost projects to the processes it starts (IntoChatOS__Hosting__Mode=run). It is a
    // child of the Hosting section, which hosted-authority detection must ignore.
    public const string ModeKey = IntoChat.OS.Framework.IntoChatOSNames.HostingModeConfigurationKey;

    private static readonly string[] UrlKeys = ["urls", "ASPNETCORE_URLS", "DOTNET_URLS"];
    private static readonly string[] PortKeys =
        ["ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS", "HTTP_PORTS", "HTTPS_PORTS"];

    public static bool IsRunMode(IConfiguration configuration)
        => string.Equals(configuration[ModeKey], IntoChat.OS.Framework.IntoChatOSNames.HostingModeRun, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);

    // True when the check applies: run mode, Open posture, no explicit opt-in.
    public static bool Applies(IConfiguration configuration)
        => IsRunMode(configuration)
            && string.Equals(configuration[PostureKey], "Open", StringComparison.OrdinalIgnoreCase)
            && !configuration.GetValue(AllowOpenOnNetworkKey, false);

    public static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Applies(configuration)) { return; }
        var offenders = NonLoopbackConfigured(configuration);
        if (offenders.Count == 0) { return; }
        throw new InvalidOperationException(
            "Open posture serves every anonymous caller as the owner, so in Aspire run mode it may listen on loopback only. Not loopback: "
            + string.Join(", ", offenders) + $". Bind 127.0.0.1/localhost, or set {AllowOpenOnNetworkKey}=true to accept network exposure.");
    }

    public static IReadOnlyList<string> NonLoopbackConfigured(IConfiguration configuration)
    {
        var found = new List<string>();
        foreach (var key in UrlKeys)
        {
            foreach (var url in Split(configuration[key]))
            { if (!IsLoopbackUrl(url)) { found.Add(url); } }
        }
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url && !IsLoopbackUrl(url)) { found.Add(url); }
        }
        // A bare port binds every interface.
        foreach (var key in PortKeys)
        {
            if (configuration[key] is { Length: > 0 } ports) { found.Add($"{key}={ports} (all interfaces)"); }
        }
        return found.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static bool IsLoopbackUrl(string url)
    {
        var rest = url.Trim();
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) { rest = rest[(scheme + 3)..]; }
        var slash = rest.IndexOf('/');
        if (slash >= 0) { rest = rest[..slash]; }
        string host;
        if (rest.StartsWith('[')) { var close = rest.IndexOf(']'); host = close > 0 ? rest[1..close] : rest; }
        else { var colon = rest.LastIndexOf(':'); host = colon >= 0 ? rest[..colon] : rest; }
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) { return true; }
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    // Registers the post-bind backstop. The exit is replaceable so a test host does not end the test process.
    public static IServiceCollection AddOpenPostureBackstop(this IServiceCollection services)
    {
        services.TryAddSingleton<OpenPostureExit>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Backstop>());
        return services;
    }

    private static IEnumerable<string> Split(string? urls)
        => (urls ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public class OpenPostureExit
    {
        public virtual void Fail() => Environment.ExitCode = 1;
    }

    private sealed class Backstop(IServiceProvider services, IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<Backstop> logger, OpenPostureExit exit)
        : IHostedLifecycleService
    {
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken)
        {
            if (!IsRunMode(configuration) || !string.Equals(configuration[PostureKey], "Open", StringComparison.OrdinalIgnoreCase)) { return Task.CompletedTask; }
            var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray();
            if (addresses is null) { return Task.CompletedTask; }
            if (addresses.Length == 0)
            {
                // Some servers publish addresses only once the application has started.
                lifetime.ApplicationStarted.Register(() => Inspect(services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? []));
                return Task.CompletedTask;
            }
            Inspect(addresses);
            return Task.CompletedTask;
        }

        private void Inspect(string[] addresses)
        {
            var exposed = addresses.Where(address => !IsLoopbackUrl(address)).ToArray();
            if (exposed.Length > 0 && !configuration.GetValue(AllowOpenOnNetworkKey, false))
            {
                logger.LogError("Open posture serves every anonymous caller as the owner and may listen on loopback only in Aspire run mode; bound {Urls}. Bind loopback or set {Key}=true.",
                    string.Join(", ", exposed), AllowOpenOnNetworkKey);
                exit.Fail();
                lifetime.StopApplication();
                return;
            }
            logger.LogInformation("Open posture listening on {Urls}", string.Join(", ", addresses));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
