using System.Security.Cryptography;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Azure;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OsNames = IntoChat.OS.Contracts.Configuration.IntoChatOSNames;

namespace IntoChat.OS.Modules.Microsoft.Orleans.Tests.Integration;

// Server hosts built (never started) through the product recipe on one topology: what IntoChatOS:Topology composes.
internal static class TopologyHosts
{
    // An Azurite-shaped account on a closed loopback port: building a cloud host makes no network call.
    public const string AzuriteConnection = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=dGVzdC1rZXk=;"
        + "BlobEndpoint=http://127.0.0.1:1/devstoreaccount1;QueueEndpoint=http://127.0.0.1:1/devstoreaccount1;TableEndpoint=http://127.0.0.1:1/devstoreaccount1;";
    public const string Vault = "https://test-vault.vault.azure.net/";

    public static WebApplicationBuilder Builder(TestHome home, IReadOnlyDictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OsNames.MasterKeyConfigurationKey] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [home.Setting.Key] = home.Setting.Value,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    public static WebApplicationBuilder Compose(WebApplicationBuilder builder, bool orleans = true, bool vaultless = false)
    {
        builder.AddIntoChatOSServer(server =>
        {
            // Ahead of the orleans module, so its cloud implementation (the production Key Vault source) is not the one selected.
            if (vaultless) { server.AddModule<VaultlessCloud>(); }
            if (orleans) { server.AddModule<OrleansModule>(); }
        });
        return builder;
    }

    // The cloud topology with its Key Vault source replaced by an empty one (tests only; production never passes one).
    [DigitalBrain.Module("vaultless-cloud")]
    private sealed class VaultlessCloud : DigitalBrain.Module, ITopologyModule
    {
        public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
        public IReadOnlyList<ITopologyProfile> TopologyProfiles { get; } =
            [new CloudProfile((configuration, _) => configuration.AddInMemoryCollection(new Dictionary<string, string?>()))];
    }
}
