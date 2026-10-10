using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Platform.Trust;

// A machine's private way into a separate trust cluster (H2, R34): an Orleans client in its own host, started while
// the silo initializes so every startup task finds it ready. Its client is never the silo's IGrainFactory or
// IClusterClient; only GrainTrustClients holds it.
internal sealed class TrustGatewayClient(IOptions<TrustOptions> options, ILoggerFactory logging)
    : ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    private IHost? _host;

    public IGrainFactory Grains => _host?.Services.GetRequiredService<IClusterClient>()
        ?? throw new InvalidOperationException("The trust gateway client is used before the silo initialized it.");

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(TrustGatewayClient), ServiceLifecycleStage.RuntimeInitialize, StartAsync, StopAsync);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_host is not null) { return; }
        var trust = options.Value;
        if (trust.Gateway is not { } gateway || !TrustOptions.TryParse(gateway, out var host, out var port))
        { throw new InvalidOperationException($"{TrustOptions.Key}:Gateway is not host:port."); }
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        var endpoint = new IPEndPoint(addresses.First(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), port);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(logging);
        builder.UseOrleansClient(client => client
            .UseStaticClustering(endpoint)
            .Configure<ClusterOptions>(cluster => (cluster.ClusterId, cluster.ServiceId) = (trust.ClusterId, trust.ServiceId)));
        _host = builder.Build();
        await _host.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_host is { } host) { await host.StopAsync(cancellationToken); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable host) { await host.DisposeAsync(); }
        _host = null;
    }
}
