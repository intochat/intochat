using DigitalBrain.Capabilities.Compute;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Runtime.Http.Surface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Runtime.Http.Connections.OAuth;

public static class OAuthConnectionsHosting
{
    public static IServiceCollection AddDigitalBrainOAuthConnections(
        this IServiceCollection services,
        Action<OAuthConnectionsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<OAuthConnectionsOptions>()
            .Configure<IServiceProvider>((options, sp) =>
                sp.GetService<IConfiguration>()?.GetSection(OAuthConnectionsOptions.SectionName).Bind(options));
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        var resolved = new OAuthConnectionsOptions();
        configure?.Invoke(resolved);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OAuthLoginRequests>();
        services.TryAddSingleton<OAuthTokenExchange>();
        services.TryAddSingleton<IOAuthTokenExchange>(sp => sp.GetRequiredService<OAuthTokenExchange>());
        services.TryAddSingleton<OAuthLoginFlow>();
        services.TryAddSingleton<OAuthDeviceRequests>();
        services.TryAddSingleton<IOAuthDeviceGrantAcceptor, NeuronProxiesOAuthDeviceGrantAcceptor>();
        services.TryAddSingleton<OAuthDeviceFlow>();
        services.TryAddSingleton<IOAuthDeviceFlow>(sp => sp.GetRequiredService<OAuthDeviceFlow>());

        services.AddHttpClient(resolved.ResolvedHttpClientName, client => client.Timeout = OAuthHttpProtocol.DefaultRequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddMeteredHttpClient(resolved.ResolvedMeteredHttpClientName)
            .ConfigureHttpClient(client => client.Timeout = OAuthHttpProtocol.DefaultRequestTimeout);
        services.AddHttpClient(resolved.ResolvedDeviceHttpClientName, client => client.Timeout = OAuthHttpProtocol.DefaultRequestTimeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpSurface, OAuthCallback>());
        // "Request starting" lines carry the callback's query (code and state); never log them, whatever the host's level.
        services.AddLogging(logging => logging.AddFilter(OAuthCallback.RequestLogCategory, LogLevel.Warning));

        return services;
    }
}
