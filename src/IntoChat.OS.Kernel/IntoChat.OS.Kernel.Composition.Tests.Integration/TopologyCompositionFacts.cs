using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Azure;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Storage;
using Orleans.Streams;
using IntoChat.OS.Kernel.Composition;
using OsNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

// IntoChatOS:Topology alone composes clustering, grain stores, streams and reminders (TOPO-01); the cluster store is the
// runtime BrainLog on every topology (host-bound, 06-02 R4-09).
public sealed class TopologyCompositionFacts
{
    private static readonly global::Orleans.Runtime.GrainId Probe = global::Orleans.Runtime.GrainId.Create("probe", "topology");

    [Fact]
    public async Task ALocalHostStoresOnFilesAndKeepsBrainLogAsTheClusterStore()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Compose(TopologyHosts.Builder(home, new Dictionary<string, string?>()));
        await using var app = builder.Build();

        Assert.IsType<LocalProfile>(app.Services.GetRequiredService<ITopologyProfile>());
        var platform = Assert.IsType<FileGrainStorage>(app.Services.GetRequiredKeyedService<IGrainStorage>(OsNames.PlatformGrainStorage));
        Assert.Equal(Path.GetFullPath(Path.Combine(home.State, "files", OsNames.PlatformGrainStorage)), platform.RootDirectory);
        Assert.IsType<FileGrainStorage>(app.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.PubSubStore));
        Assert.IsType<FileReminderTable>(app.Services.GetRequiredService<IReminderTable>());
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IStreamProvider) && Equals(descriptor.ServiceKey, IntoChatOSNames.SignalStreams));
        AssertBrainLogIsTheClusterStore(builder.Services, app.Services);
        Assert.Null(app.Services.GetService<CloudStorageAccount>());
    }

    [Fact]
    public async Task ALocalHostKeepsGrainStateAcrossARebuildOfTheSameHome()
    {
        using var home = TestHome.Create();
        var settings = new Dictionary<string, string?>();
        await using (var first = TopologyHosts.Compose(TopologyHosts.Builder(home, settings)).Build())
        {
            var state = new GrainState<string> { State = "kept" };
            await first.Services.GetRequiredKeyedService<IGrainStorage>(OsNames.PlatformGrainStorage).WriteStateAsync("probe", Probe, state);
        }
        await using var second = TopologyHosts.Compose(TopologyHosts.Builder(home, settings)).Build();
        var read = new GrainState<string>();
        await second.Services.GetRequiredKeyedService<IGrainStorage>(OsNames.PlatformGrainStorage).ReadStateAsync("probe", Probe, read);
        Assert.True(read.RecordExists);
        Assert.Equal("kept", read.State);
    }

    [Fact]
    public async Task ACloudHostBindsTheAzureStoresAndKeepsBrainLogAsTheClusterStore()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Compose(TopologyHosts.Builder(home, new Dictionary<string, string?>
        {
            [OsNames.TopologyKey] = OsNames.TopologyCloud,
            [OsNames.CloudStorageConfigurationKey] = TopologyHosts.AzuriteConnection,
            [OsNames.CloudKeyVaultConfigurationKey] = TopologyHosts.Vault,
        }), vaultless: true);
        await using var app = builder.Build();

        Assert.IsType<CloudProfile>(app.Services.GetRequiredService<ITopologyProfile>());
        Assert.NotNull(app.Services.GetRequiredService<CloudStorageAccount>());
        Assert.Contains("AzureBlob", app.Services.GetRequiredKeyedService<IGrainStorage>(OsNames.PlatformGrainStorage).GetType().Name, StringComparison.Ordinal);
        Assert.Contains("Azure", app.Services.GetRequiredService<IReminderTable>().GetType().FullName!, StringComparison.Ordinal);
        Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IStreamProvider) && Equals(descriptor.ServiceKey, IntoChatOSNames.SignalStreams));
        AssertBrainLogIsTheClusterStore(builder.Services, app.Services);
        // The platform's deployment identity and cookie keys reach the deployment's grain-state container.
        var container = app.Services.GetRequiredKeyedService<global::Azure.Storage.Blobs.BlobContainerClient>(OsNames.GrainState);
        Assert.Equal(OsNames.GrainStateContainer, container.Name);
        Assert.Equal("devstoreaccount1", container.AccountName);
    }

    // The runtime BrainLog is the cluster store (06-02, R4-09); unmigrated Framework grains reach the same logs through the
    // legacy store, which the kernel registration wraps.
    private static void AssertBrainLogIsTheClusterStore(IServiceCollection services, IServiceProvider provider)
    {
        Assert.True(KernelStorage.IsComposed(services));
        Assert.True(ClusterStoreHosting.HasClusterStore(services));
        var cluster = provider.GetRequiredKeyedService<IGrainStorage>(OsNames.ClusterGrainStorage);
        Assert.Equal("DigitalBrain.Runtime.Orleans.BrainGrainStorage", cluster.GetType().FullName);
        Assert.NotNull(provider.GetRequiredService<IBrainJournalWriter>());
        Assert.True(BrainHosting.HasKernelRegistration(provider));
        Assert.NotNull(provider.GetRequiredKeyedService<IGrainStorage>(OsNames.LegacyClusterGrainStorage));
        Assert.IsType<LegacyPersistentStateFactory>(provider.GetRequiredService<global::Orleans.Runtime.IPersistentStateFactory>());
        Assert.IsType<LegacySignalJournal>(provider.GetRequiredService<global::IntoChat.OS.Framework.ISignalJournal>());
    }
}
