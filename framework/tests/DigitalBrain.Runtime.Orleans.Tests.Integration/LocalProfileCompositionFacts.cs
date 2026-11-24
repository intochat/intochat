using DigitalBrain.Hosting;
using DigitalBrain.Modules;
using DigitalBrain.Runtime.Orleans.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;

using OrleansSdk = Orleans;
namespace DigitalBrain.Runtime.Orleans.Tests.Integration;

public sealed class LocalProfileCompositionFacts
{
    private static InProcessHostOptions Options(string home, params Type[] modules) => new() { Home = home, Modules = modules };

    [Fact]
    public void With_the_host_registration_the_cluster_store_is_the_host_store_and_named_stores_are_memory()
    {
        using var home = new TempHome();
        var fake = new FakeClusterStore();
        using var host = new LocalSiloHostFactory(fake).Build(Options(home.Path, typeof(DriveModule)), home.Path);

        Assert.Same(fake, host.Services.GetRequiredKeyedService<IGrainStorage>(RuntimeOptions.Default.ClusterGrainStorage));
        Assert.Equal(1, fake.Registered);
        foreach (var name in new[] { RuntimeOptions.Default.PlatformGrainStorage, "drive" })
        {
            var store = host.Services.GetRequiredKeyedService<IGrainStorage>(name);
            Assert.NotNull(store);
        }
        Assert.NotNull(host.Services.GetRequiredService<IReminderTable>());
        Assert.False(string.IsNullOrEmpty(host.Services.GetRequiredService<IConfiguration>()[RuntimeOptions.Default.MasterKeyKey]));
    }

    [Fact]
    public async Task With_the_local_topology_alone_the_cluster_store_throws_naming_AddBrainStorage()
    {
        using var home = new TempHome();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [RuntimeOptions.Default.HomeKey] = home.Path });
        var context = TopologyContext.Create(Topology.Local, StateHome.Resolve(builder.Configuration, RuntimeOptions.Default), builder.Configuration, []);
        builder.UseOrleans(silo => new LocalProfile().ConfigureSilo(silo, context));
        using var host = builder.Build();

        var cluster = host.Services.GetRequiredKeyedService<IGrainStorage>(RuntimeOptions.Default.ClusterGrainStorage);

        Assert.IsType<UnregisteredClusterStorage>(cluster);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.WriteStateAsync("s", GrainId.Create("probe", "k"), new GrainState<int>()));
        Assert.Contains("AddBrainStorage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_grain_store_with_a_reserved_name_is_an_issue_naming_the_module()
    {
        const string name = "platform";
        var module = new ModuleManifest([], [], [Need.State(name)], [], []) { Id = "greedy" };
        var issue = Assert.Single(new LocalProfile().Validate(new ConfigurationBuilder().Build(), [module]));
        Assert.Contains("greedy", issue.Key, StringComparison.Ordinal);
        Assert.Contains(name, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_need_kind_is_bound(NeedKind kind) => Assert.True(new LocalProfile().Bindings.Binds(kind));

    public static TheoryData<NeedKind> Kinds() => [.. Enum.GetValues<NeedKind>()];

    [Fact]
    public void The_cluster_store_is_host_bound() => Assert.Equal(ClusterStoreBinding.HostBound, new LocalProfile().Bindings.ClusterStore);

    [Fact]
    public void Services_describe_one_os_process_with_the_silo_port_no_gateway_and_the_home_volume()
    {
        var os = Assert.Single(new LocalProfile().Services.Processes, p => p.Name == "os");
        Assert.Contains(11111, os.Ports);
        Assert.DoesNotContain(30000, os.Ports);
        Assert.Contains(LocalProfile.HttpPort, os.Ports);
        Assert.Equal(["<Home>"], os.Volumes);
        Assert.Contains("DigitalBrain__Topology", os.EnvironmentKeys);
        Assert.Contains("DigitalBrain__Home", os.EnvironmentKeys);
        Assert.Empty(new LocalProfile().Services.AzureResourceKinds);
    }

    // Orleans 10: EndpointOptions.GatewayPort 0 opens no gateway listener, and the silo's own co-hosted client still calls grains.
    [Fact]
    public async Task With_no_gateway_port_the_silo_opens_no_gateway_and_still_serves_its_own_calls()
    {
        using var home = new TempHome();
        using var lease = PortReservation.ReservePair();
        using var host = new LocalSiloHostFactory(new FakeClusterStore()).Build(Options(home.Path), home.Path, lease);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(TopologyContext.NoGateway, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OrleansSdk.Configuration.EndpointOptions>>().Value.GatewayPort);
            Assert.Null(host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress);
            var management = host.Services.GetRequiredService<IGrainFactory>().GetGrain<IManagementGrain>(0);
            var hosts = await management.GetHosts(true, TestContext.Current.CancellationToken);
            Assert.NotEmpty(hosts);
            Assert.True(Listens(lease.SiloPort), "The silo port listens.");
            Assert.False(Listens(lease.GatewayPort), "Nothing listens where a gateway would.");
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task A_configured_gateway_port_opens_the_gateway()
    {
        using var home = new TempHome();
        using var lease = PortReservation.ReservePair();
        var options = Options(home.Path);
        options.Configuration = new Dictionary<string, string?> { [TopologyContext.GatewayPortKey] = lease.GatewayPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        using var host = new LocalSiloHostFactory(new FakeClusterStore()).Build(options, home.Path, lease);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(lease.GatewayPort, host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress?.Endpoint.Port);
            Assert.True(Listens(lease.GatewayPort), "The configured gateway listens.");
        }
        finally { await host.StopAsync(TestContext.Current.CancellationToken); }
    }

    // A loopback TCP connect to a port this fact reserved itself; nothing else on the machine is probed.
    private static bool Listens(int port)
    {
        using var client = new System.Net.Sockets.TcpClient();
        try
        {
            client.Connect(System.Net.IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    [Fact]
    public void The_local_topology_is_built_in()
    {
        using var home = new TempHome();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RuntimeOptions.Default.TopologyKey] = "local",
            [RuntimeOptions.Default.HomeKey] = home.Path,
        });
        builder.UseClusterStore(new FakeClusterStore());
        Assert.IsType<LocalProfile>(builder.UseTopology([], []));
    }
}
