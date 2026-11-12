using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QdrantSdk = Qdrant;
namespace IntoChat.OS.Modules.Qdrant;

[DigitalBrain.Modules.Module(QdrantModule.ManifestName)]
public sealed class QdrantModule : DigitalBrain.Modules.Module
{
    internal const string ManifestName = "qdrant";

    public static DigitalBrain.Modules.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Modules.Setting.Of<QdrantModuleOptions>(DigitalBrain.Modules.Configuration.ModuleConfiguration.ModuleOptionsSection(QdrantModule.ManifestName))],
        Secrets: []);

    public static bool RequiresContainer => true;

    public const string ConnectionName = "qdrant";

    public static bool IsConnected(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));

    // Without a connection IVectorStore is served from memory, so search still works; nothing survives a restart.
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => QdrantConnection.CreateClient(
            configuration.GetConnectionString(ConnectionName)!));
        services.TryAddSingleton<IVectorStore>(provider => IsConnected(configuration)
            ? new QdrantStore(provider.GetRequiredService<QdrantSdk.Client.QdrantClient>()) : new InMemoryQdrant());
    }
}
