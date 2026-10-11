using DigitalBrain.Framework;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Testing.Module;

// A trust cluster of its own (H2) for facts that run machines against it: one silo with only the trust grains,
// memory platform storage and 8 seed nodes; each machine provisions its own default owner at boot (R59). Machines join it through
// TestBrain.WithTrust, each over its own trust link to the host's link endpoint (T20-7) on loopback, beside the relay.
public sealed class TrustHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TrustHost(IHost host, string pairingSecret)
    {
        (_host, PairingSecret) = (host, pairingSecret);
        var (link, relay, trustId) = host.Services.TrustLinks();
        (Endpoint, Relay, TrustId) = ($"127.0.0.1:{link.Port}", relay, trustId);
    }

    // host:port machines dial (DigitalBrain:Platform:Trust:Endpoint).
    public string Endpoint { get; }

    public System.Net.IPEndPoint Relay { get; }

    // What a machine pins (T20-3).
    public string TrustId { get; }

    public IServiceProvider Services => _host.Services;

    // One per test host (R52): every machine of the fact joins the default owner's account with it.
    public string PairingSecret { get; }

    // The owner's custody root, shared by the fact's machines the way an owner's devices share it (Task 19
    // moves it into hardware): a part key wrapped on one machine unwraps on the other.
    public string MasterKey { get; } = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public IGrainFactory Grains => _host.Services.GetRequiredService<IGrainFactory>();

    public static async Task<TrustHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var siloPort = FreePort();
        var id = $"trust-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [$"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}"] = "8",
            [$"{DigitalBrain.Platform.Trust.TrustOptions.Key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.IdentityPath)}"] = Path.Combine(Path.GetTempPath(), id, "identity.key"),
            [$"{DigitalBrain.Platform.Trust.TrustOptions.Key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.AdvertisedAddress)}"] = "127.0.0.1",
        });
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort: 0, serviceId: id, clusterId: id);
            silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);
            silo.AddTrustCluster().AddTrustBoundary().AddTrustLinks();
        });
        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return new TrustHost(host, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync().ConfigureAwait(false); }
        finally { _host.Dispose(); }
    }
}

public static class TrustHostExtensions
{
    // This host is a machine of a separate trust cluster (H2, T20-7): it reaches the trust grains only over its trust
    // link and pairs with the trust host's other machines through its pairing secret (R52).
    public static TestBrain WithTrust(this TestBrain brain, TrustHost trust)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(trust);
        var key = DigitalBrain.Platform.Trust.TrustOptions.Key;
        return brain
            .WithConfiguration($"{key}:{nameof(DigitalBrain.Platform.Trust.TrustOptions.Endpoint)}", trust.Endpoint)
            .WithConfiguration($"{DigitalBrain.Platform.Parts.PartsOptions.Key}:{nameof(DigitalBrain.Platform.Parts.PartsOptions.PairingSecret)}", trust.PairingSecret)
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, trust.MasterKey);
    }
}
