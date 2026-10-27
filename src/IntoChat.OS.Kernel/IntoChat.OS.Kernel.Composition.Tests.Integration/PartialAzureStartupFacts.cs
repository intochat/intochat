using DigitalBrain.Runtime.Orleans;
using OsNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

// A partial or unknown topology stops the host before the silo exists, naming every key and never a value (TOPO-01, T-03-55, T-03-57).
public sealed class PartialAzureStartupFacts
{
    private const string Secret = "AccountKey=never-in-a-message";

    public static TheoryData<string, string?, string?> PartialCloudSets() => new()
    {
        { OsNames.CloudStorageConfigurationKey, null, TopologyHosts.Vault },
        { OsNames.CloudKeyVaultConfigurationKey, TopologyHosts.AzuriteConnection, "http://plain.vault.azure.net/" },
    };

    [Theory]
    [MemberData(nameof(PartialCloudSets))]
    public void APartialCloudSetStopsTheHostNamingTheKey(string named, string? storage, string? vault)
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?>
        {
            [OsNames.TopologyKey] = OsNames.TopologyCloud,
            [OsNames.CloudStorageConfigurationKey] = storage,
            [OsNames.CloudKeyVaultConfigurationKey] = vault,
        });
        var error = Assert.Throws<InvalidOperationException>(() => TopologyHosts.Compose(builder));
        Assert.Contains(named, error.Message, StringComparison.Ordinal);
    }

    // The key vault is optional on cloud since the runtime's 04-06 (R4-08: contributor runs on Azurite use the machine key);
    // a missing storage account still stops the host, and a key vault that is set must be an https URI (above).
    [Fact]
    public void AMissingStorageIsNamedAndAMissingVaultIsNot()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?> { [OsNames.TopologyKey] = OsNames.TopologyCloud });
        var error = Assert.Throws<InvalidOperationException>(() => TopologyHosts.Compose(builder));
        Assert.Contains(OsNames.CloudStorageConfigurationKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OsNames.CloudKeyVaultConfigurationKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableStorageValueIsNamedWithoutItsValue()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?>
        {
            [OsNames.TopologyKey] = OsNames.TopologyCloud,
            [OsNames.CloudStorageConfigurationKey] = Secret,
            [OsNames.CloudKeyVaultConfigurationKey] = TopologyHosts.Vault,
        });
        var error = Assert.Throws<InvalidOperationException>(() => TopologyHosts.Compose(builder));
        Assert.Contains(OsNames.CloudStorageConfigurationKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudWithoutTheOrleansModuleNamesTheAzureRuntimePackage()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?>
        {
            [OsNames.TopologyKey] = OsNames.TopologyCloud,
            [OsNames.CloudStorageConfigurationKey] = TopologyHosts.AzuriteConnection,
            [OsNames.CloudKeyVaultConfigurationKey] = TopologyHosts.Vault,
        });
        var error = Assert.Throws<InvalidOperationException>(() => TopologyHosts.Compose(builder, orleans: false));
        Assert.Contains(TopologyHosting.CloudPackage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownTopologyStopsTheHostNamingTheKey()
    {
        using var home = TestHome.Create();
        var builder = TopologyHosts.Builder(home, new Dictionary<string, string?> { [OsNames.TopologyKey] = "moon" });
        var error = Assert.Throws<InvalidOperationException>(() => TopologyHosts.Compose(builder));
        Assert.Contains(OsNames.TopologyKey, error.Message, StringComparison.Ordinal);
    }
}
