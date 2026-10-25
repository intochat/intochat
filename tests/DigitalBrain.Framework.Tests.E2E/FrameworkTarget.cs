using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Azure;
using DigitalBrain.Runtime.Orleans.Azure.Tests.Integration;
using DigitalBrain.Spi;
using DigitalBrain.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Framework.Tests.E2E;

/// <summary>A place the framework e2e facts run: it starts an in-process host that survives a restart.</summary>
public abstract class FrameworkTarget
{
    /// <summary>The target names the theory data lists. Phase 4 adds the TestOS target here.</summary>
    public static TheoryData<string> Names => new(LocalOrleansTarget.TargetName, AzuriteOrleansTarget.TargetName);

    public abstract string Name { get; }

    public abstract Task<IInProcessHost> Start(CancellationToken ct);

    public static FrameworkTarget For(string name, AzuriteFixture azurite) => name switch
    {
        LocalOrleansTarget.TargetName => new LocalOrleansTarget(),
        AzuriteOrleansTarget.TargetName => new AzuriteOrleansTarget(azurite),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown framework target."),
    };

    public override string ToString() => Name;
}

/// <summary>The local topology through <c>SiloHost.Local()</c> (LocalProfile on a temp Home).</summary>
public sealed class LocalOrleansTarget : FrameworkTarget
{
    public const string TargetName = "local-orleans";

    public override string Name => TargetName;

    public override Task<IInProcessHost> Start(CancellationToken ct) => SiloHost.Local(ct: ct);
}

/// <summary>The cloud topology (CloudProfile) against Azurite; Key Vault is replaced by an in-memory master key.</summary>
public sealed class AzuriteOrleansTarget(AzuriteFixture azurite) : FrameworkTarget
{
    public const string TargetName = "azurite-orleans";

    public override string Name => TargetName;

    public override async Task<IInProcessHost> Start(CancellationToken ct)
    {
        var host = new CloudHost(azurite.Require());
        try { await host.Start(ct); }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
        return host;
    }

    private sealed class CloudHost(string storage) : IInProcessHost
    {
        private const string MasterKey = "e2e-master-key-from-the-in-memory-vault";
        private readonly string _clusterId = "e2e" + Guid.NewGuid().ToString("N")[..12];
        private IHost? _host;

        public IServiceProvider Services => (_host ?? throw new ObjectDisposedException(nameof(CloudHost))).Services;

        public string Home { get; } = Path.Combine(Path.GetTempPath(), "db-e2e-" + Guid.NewGuid().ToString("N"));

        public async Task Start(CancellationToken ct)
        {
            _host = Build();
            await _host.StartAsync(ct);
        }

        public async Task Restart(CancellationToken ct)
        {
            await Stop(ct);
            await Start(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Stop(CancellationToken.None);
            if (Directory.Exists(Home)) { Directory.Delete(Home, recursive: true); }
        }

        private async Task Stop(CancellationToken ct)
        {
            if (_host is null) { return; }
            var host = _host;
            _host = null;
            try { await host.StopAsync(ct); }
            finally { host.Dispose(); }
        }

        private IHost Build()
        {
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = "cloud",
                [IntoChatOSNames.HomeKey] = Home,
                [IntoChatOSNames.CloudStorageKey] = storage,
                [IntoChatOSNames.CloudKeyVaultKey] = "https://e2e-vault.vault.azure.net/",
                [TopologyContext.SiloPortKey] = FreePort().ToString(CultureInfo.InvariantCulture),
                [TopologyContext.GatewayPortKey] = FreePort().ToString(CultureInfo.InvariantCulture),
            });
            builder.UseClusterStore(_ => { });
            var cloud = new CloudProfile((configuration, _) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [IntoChatOSNames.MasterKeyKey] = MasterKey }));
            builder.UseTopology([], [], cloud);
            builder.UseOrleans(silo =>
            {
                silo.ConfigureTopologySilo();
                silo.Configure<global::Orleans.Configuration.ClusterOptions>(o => { o.ClusterId = _clusterId; o.ServiceId = _clusterId; });
                silo.Configure<global::Orleans.Configuration.EndpointOptions>(o => o.AdvertisedIPAddress = IPAddress.Loopback);
            });
            return builder.Build();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
