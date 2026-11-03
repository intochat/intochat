using DigitalBrain.Modules.Options;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Qdrant.Aspire.Hosting;

public sealed class QdrantModuleHosting : ModuleHosting<IntoChat.OS.Modules.Qdrant.QdrantModule>
{
    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var options = brain.GetModuleConfiguration("qdrant").GetModuleOptions<QdrantModuleOptions>("qdrant");
        if (!options.Host) { return; }
        var module = new IntoChatOSModuleBuilder<IntoChat.OS.Modules.Qdrant.Aspire.Hosting.QdrantModuleHosting>(brain);
        var qdrant = brain.ApplicationBuilder.AddQdrant(brain.ResourceName("qdrant-server"))
            .WithParentRelationship(module.Resource)
            .WithDataVolume()
            .WithLifetime(ContainerLifetime.Persistent);
        module.AddProjection(new QdrantProjection(qdrant));
    }

    private sealed class QdrantProjection(IResourceBuilder<QdrantServerResource> qdrant) : IntoChatOSModuleProjection
    {
        // WaitAnnotation: Apply targets IResourceWithEnvironment (kernel), not always IResourceWithWaitSupport.
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder
                .WithReference(qdrant, connectionName: "qdrant")
                .WithAnnotation(new WaitAnnotation(qdrant.Resource, WaitType.WaitUntilHealthy, exitCode: 0));
        }
    }
}
