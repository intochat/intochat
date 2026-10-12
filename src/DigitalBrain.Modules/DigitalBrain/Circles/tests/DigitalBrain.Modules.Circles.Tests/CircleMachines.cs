using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Circles.Tests;

// R149: own machines of one trust cluster, each with its own custody root (a person's machine opens only its own
// account's core), running the Apps and Circles modules and the scripted connector.
internal sealed class CircleMachines : IAsyncDisposable
{
    private readonly IHost _trust;
    private readonly string _endpoint;
    private readonly string _pairing = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private readonly List<ITestBrain> _machines = [];

    private CircleMachines(IHost trust)
    {
        _trust = trust;
        _endpoint = $"127.0.0.1:{trust.Services.TrustLinks().Link.Port}";
    }

    public static async Task<CircleMachines> Start(CancellationToken ct)
    {
        var id = $"trust-{Guid.NewGuid():N}";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthOptions.PostureKey] = nameof(IdentityPosture.Open),
            [$"{DigitalBrain.Framework.Links.LinkOptions.Key}:{nameof(DigitalBrain.Framework.Links.LinkOptions.RegistrationsPerAddressPerMinute)}"] = "1000",
            [$"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}"] = "8",
            [$"{TrustOptions.Key}:{nameof(TrustOptions.IdentityPath)}"] = Path.Combine(Path.GetTempPath(), id, "identity.key"),
            [$"{TrustOptions.Key}:{nameof(TrustOptions.AdvertisedAddress)}"] = "127.0.0.1",
        });
        var port = FreePort();
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(port, gatewayPort: 0, serviceId: id, clusterId: id);
            silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);
            silo.AddTrustCluster().AddTrustBoundary().AddTrustLinks();
        });
        var host = builder.Build();
        await host.StartAsync(ct);
        return new CircleMachines(host);
    }

    // An own machine with its own custody root.
    public async Task<ITestBrain> Machine(CancellationToken ct, Func<TestBrain, TestBrain>? configure = null)
    {
        var machine = await CircleBrainRooms.Start(ct, brain => (configure?.Invoke(brain) ?? brain)
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Own))
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.SeedNodes)}", "8")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.DataShards)}", "4")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ParityShards)}", "2")
            .WithConfiguration($"{MeshOptions.Key}:{nameof(MeshOptions.ManifestReplicas)}", "2")
            .WithConfiguration($"{TrustOptions.Key}:{nameof(TrustOptions.Endpoint)}", _endpoint)
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.PairingSecret)}", _pairing)
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        _machines.Add(machine);
        return machine;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var machine in Enumerable.Reverse(_machines)) { await machine.DisposeAsync(); }
        try { await _trust.StopAsync(); }
        finally { _trust.Dispose(); }
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
