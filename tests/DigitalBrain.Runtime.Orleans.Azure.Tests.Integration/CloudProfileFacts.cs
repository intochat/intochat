using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.Streams;

namespace DigitalBrain.Runtime.Orleans.Azure.Tests.Integration;

public sealed class CloudProfileFacts
{
    private static IConfiguration Configuration(string? storage, string? vault) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.CloudStorageKey] = storage,
            [IntoChatOSNames.CloudKeyVaultKey] = vault,
        })
        .Build();

    [Fact]
    public void Missing_storage_and_key_vault_are_one_issue_each()
    {
        var issues = new CloudProfile().Validate(Configuration(null, null), []).ToList();

        Assert.Equal(2, issues.Count);
        Assert.Contains(issues, i => i.Key == IntoChatOSNames.CloudStorageKey);
        Assert.Contains(issues, i => i.Key == IntoChatOSNames.CloudKeyVaultKey);
    }

    [Theory]
    [InlineData("http://vault.vault.azure.net/")]
    [InlineData("vault.vault.azure.net")]
    [InlineData("/relative")]
    public void A_key_vault_that_is_not_an_https_uri_is_an_issue(string vault)
    {
        var issue = Assert.Single(new CloudProfile().Validate(Configuration("UseDevelopmentStorage=true", vault), []));
        Assert.Equal(IntoChatOSNames.CloudKeyVaultKey, issue.Key);
    }

    [Fact]
    public void A_valid_configuration_has_no_issues()
        => Assert.Empty(new CloudProfile().Validate(Configuration("UseDevelopmentStorage=true", CloudHosts.Vault), []));

    [Theory]
    [InlineData("platform")]
    [InlineData("pubsubstore")]
    public void A_module_grain_store_with_a_reserved_name_is_an_issue_naming_the_module(string name)
    {
        var module = new ModuleManifest([], [], [Need.GrainStore(name)], [], []) { Id = "greedy" };
        var issue = Assert.Single(new CloudProfile().Validate(Configuration("UseDevelopmentStorage=true", CloudHosts.Vault), [module]));
        Assert.Contains("greedy", issue.Key, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_need_kind_is_bound(NeedKind kind) => Assert.True(new CloudProfile().Bindings.Binds(kind));

    public static TheoryData<NeedKind> Kinds() => [.. Enum.GetValues<NeedKind>()];

    [Fact]
    public void Bindings_are_the_azure_ones_and_the_cluster_store_is_host_bound()
    {
        var bindings = new CloudProfile().Bindings;
        Assert.Equal(7, Enum.GetValues<NeedKind>().Length);
        Assert.Equal(ClusteringBinding.AzureTable, bindings.Clustering);
        Assert.Equal(GrainStoreBinding.AzureBlob, bindings.GrainStores);
        Assert.Equal(StreamsBinding.AzureQueue, bindings.Streams);
        Assert.Equal(RemindersBinding.AzureTable, bindings.Reminders);
        Assert.Equal(SecretsBinding.KeyVault, bindings.Secrets);
        Assert.Equal(HttpBinding.Direct, bindings.Http);
        Assert.Equal(SandboxBinding.HostedContainer, bindings.Sandbox);
        Assert.Equal(ModelBinding.ConfiguredEndpoint, bindings.Model);
        Assert.Equal(UiBinding.HostedWeb, bindings.Ui);
        Assert.Equal(ClusterStoreBinding.HostBound, bindings.ClusterStore);
    }

    [Fact]
    public void Services_list_the_processes_and_the_azure_resource_kinds()
    {
        var services = new CloudProfile().Services;
        Assert.Equal(["os", "os-sandbox"], services.Processes.Select(p => p.Name));
        Assert.Equal(["Microsoft.Storage/storageAccounts", "Microsoft.KeyVault/vaults", "Microsoft.App/managedEnvironments"], services.AzureResourceKinds);
        var os = services.Processes[0];
        Assert.Contains("IntoChatOS__Cloud__Storage", os.EnvironmentKeys);
        Assert.Contains("IntoChatOS__Cloud__KeyVault", os.EnvironmentKeys);
    }

    [Fact]
    public void AddCloudTopology_returns_the_production_profile()
        => Assert.Equal(Topology.Cloud, Assert.IsType<CloudProfile>(CloudTopologyExtensions.AddCloudTopology()).Topology);

    [Fact]
    public void Without_the_azure_profile_cloud_names_the_package()
    {
        using var home = new TempHome();
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [IntoChatOSNames.TopologyKey] = "cloud",
            [IntoChatOSNames.HomeKey] = home.Path,
        });
        var error = Assert.Throws<InvalidOperationException>(() => builder.UseTopology([], []));
        Assert.Contains(TopologyHosting.CloudPackage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_built_host_registers_every_cloud_binding_without_touching_the_network()
    {
        using var home = new TempHome();
        var fake = new FakeClusterStore();
        // Port 1 on loopback: any network call during registration or resolution would fail.
        const string unreachable = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;QueueEndpoint=http://127.0.0.1:1/devstoreaccount1;TableEndpoint=http://127.0.0.1:1/devstoreaccount1;";
        using var host = CloudHosts.Build(unreachable, home.Path, fake, "facts", typeof(DriveModule));
        var services = host.Services;

        Assert.Same(fake, services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage));
        foreach (var name in new[] { IntoChatOSNames.PlatformGrainStorage, IntoChatOSNames.PubSubStore, "drive" })
        {
            Assert.IsType<AzureBlobGrainStorage>(services.GetRequiredKeyedService<IGrainStorage>(name));
            Assert.Equal(IntoChatOSNames.GrainStateContainer, services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AzureBlobStorageOptions>>().Get(name).ContainerName);
        }
        Assert.Contains("Azure", services.GetRequiredService<IMembershipTable>().GetType().Name, StringComparison.Ordinal);
        Assert.Contains("Azure", services.GetRequiredService<IReminderTable>().GetType().Name, StringComparison.Ordinal);
        Assert.NotNull(services.GetRequiredKeyedService<IStreamProvider>(IntoChatOSNames.SignalStreams));
        var queues = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AzureQueueOptions>>().Get(IntoChatOSNames.SignalStreams);
        Assert.Equal(["signals-0"], queues.QueueNames);
        Assert.NotNull(queues.QueueServiceClient);
        Assert.IsType<BlobLeaseBrainLogOwnership>(services.GetRequiredService<IBrainLogOwnership>());
        Assert.Same(services.GetRequiredService<IBrainLogOwnership>(), services.GetRequiredService<IBrainOwnershipCounters>());
        Assert.Equal(CloudHosts.MasterKey, services.GetRequiredService<IConfiguration>()[IntoChatOSNames.MasterKeyKey]);
    }
}
