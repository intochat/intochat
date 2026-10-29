using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

using QdrantSdk = Qdrant;
namespace IntoChat.OS.Modules.Qdrant;

public sealed class QdrantModule() : Module<QdrantModuleOptions>(new ModuleId("qdrant"))
{
    internal const string ManifestName = "qdrant";

    // R4-04: what the module needs from its host. It has no neurons; its registrations stay in Configure until the Framework
    // leaves (06-08).
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [],
        Needs: [],
        Settings: [DigitalBrain.Setting.Of<QdrantModuleOptions>(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(QdrantModule.ManifestName))],
        Secrets: []);

    public override bool RequiresContainer => true;

    public const string ConnectionName = "qdrant";

    public static bool IsConnected(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));

    // Without a connection IVectorStore is served from memory, so search still works; nothing survives a restart.
    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => QdrantConnection.CreateClient(
            configuration.GetConnectionString(ConnectionName)!));
        services.TryAddSingleton<IVectorStore>(provider => IsConnected(configuration)
            ? new QdrantStore(provider.GetRequiredService<QdrantSdk.Client.QdrantClient>()) : new InMemoryQdrant());
    }
}
