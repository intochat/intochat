using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Testing.Module;

// A trust cluster of its own (H2) for facts that run machines against it: one silo with only the trust grains,
// memory platform storage, 8 seed nodes and the default owner on record. Machines join it through
// ModuleTestBuilder.WithTrust, each with its own private gateway client. It is a real silo on TCP ports, not an
// InProcessTestCluster: those connect over an in-memory transport no outside client can reach.
public sealed class TrustHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TrustHost(IHost host, int gatewayPort, string clusterId, string serviceId, string pairingSecret) =>
        (_host, GatewayPort, ClusterId, ServiceId, PairingSecret) = (host, gatewayPort, clusterId, serviceId, pairingSecret);

    public int GatewayPort { get; }

    public string ClusterId { get; }

    public string ServiceId { get; }

    // One per test host (R52): every machine of the fact joins the default owner's account with it.
    public string PairingSecret { get; }

    public IGrainFactory Grains => _host.Services.GetRequiredService<IGrainFactory>();

    public static async Task<TrustHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var (siloPort, gatewayPort) = (FreePort(), FreePort());
        var id = $"trust-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [$"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}"] = "8",
        });
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort, serviceId: id, clusterId: id);
            silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);
            silo.AddTrustCluster();
        });
        var host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            var owner = ModuleTest.DefaultAccount;
            await host.Services.GetRequiredService<IGrainFactory>().GetGrain<IBrainAuthority>(BrainScope.Create(owner, owner).Id).ProvisionOwner(new Member
            {
                PrincipalId = owner, AccountId = owner, BrainId = owner, Role = MemberRole.Owner, DisplayName = owner,
            }).ConfigureAwait(false);
            return new TrustHost(host, gatewayPort, id, id, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
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
