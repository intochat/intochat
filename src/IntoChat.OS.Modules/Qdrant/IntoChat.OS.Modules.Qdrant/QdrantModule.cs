using IntoChat.OS;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Hosting;

namespace IntoChat.OS.Modules.Qdrant;

public sealed class QdrantModule() : Module<QdrantModuleOptions>(new ModuleId("qdrant"))
{
    public override bool RequiresContainer => true;

    public const string ConnectionName = "qdrant";

    public static bool IsConnected(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));

    // Without a connection IVectorStore is served from memory, so search still works; nothing survives a restart.
    public override void Configure(ISiloBuilder silo)
    {
        ArgumentNullException.ThrowIfNull(silo);
        silo.Services.TryAddSingleton(_ => QdrantConnection.CreateClient(
            silo.Configuration.GetConnectionString(ConnectionName)!));
        silo.Services.TryAddSingleton<IVectorStore>(provider => IsConnected(silo.Configuration)
            ? new QdrantStore(provider.GetRequiredService<global::Qdrant.Client.QdrantClient>()) : new InMemoryQdrant());
    }
}
