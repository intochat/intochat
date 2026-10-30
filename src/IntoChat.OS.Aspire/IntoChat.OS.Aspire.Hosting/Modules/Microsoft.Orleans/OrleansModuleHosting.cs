using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Microsoft.Orleans;

public sealed class OrleansModuleHosting : ModuleHosting<OrleansModule>
{
    private static readonly string GrainStateContainerKey =
        $"{IntoChatOSConfiguration.ModuleOptionsSection("orleans")}:{nameof(OrleansOptions.GrainStateContainer)}";

    public override void Configure(IntoChatOSBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var configuration = brain.GetModuleConfiguration("orleans");
        var options = configuration.GetModuleOptions<OrleansOptions>("orleans");
        // One source for the silo: the orleans module option. A deployment that did not choose one
        // explicitly carries its identity's container (a revision runs in its own copy).
        if (!HostConfiguration.IsModuleOptionSet(configuration, "orleans", nameof(OrleansOptions.GrainStateContainer)))
        { brain.AddProjection(new GrainStateContainer(brain.GrainStateContainer)); }
        if (!options.Azurite.Enabled) { return; }

        var builder = brain.ApplicationBuilder;
        var persist = options.Azurite.PersistentStorage;
        var storage = builder.AddAzureStorage(brain.ResourceName(IntoChatOSNames.Storage))
            .RunAsEmulator(emulator =>
            {
                emulator.WithArgs("--silent");
                if (persist) { emulator.WithLifetime(ContainerLifetime.Persistent); }
                if (options.Azurite.DataVolume is { } volume) { emulator.WithDataVolume(volume); }
                else if (persist) { emulator.WithDataVolume(); }
            })
            .WithParentRelationship(brain.GetOrAddModuleNode(OrleansModule.Integration.Id));
        var clustering = storage.AddTables(brain.ResourceName(IntoChatOSNames.Clustering));
        var reminders = storage.AddTables(brain.ResourceName(IntoChatOSNames.Reminders));
        var grainState = storage.AddBlobs(brain.ResourceName(IntoChatOSNames.GrainState));
        // The Aspire Orleans resource keeps development clustering: storage on it would project
        // Orleans__* provider keys into the silo, whose clustering the cloud topology configures instead.
        // Orleans clients find the tables through the builder's Clustering (WithReference(client)).
        brain.Storage = storage;
        brain.GrainState = grainState;
        brain.Clustering = clustering;
        brain.Reminders = reminders;
        brain.ClusteringResourceName = clustering.Resource.Name;
        foreach (var resource in new IResource[] { storage.Resource, clustering.Resource, reminders.Resource, grainState.Resource })
        { brain.RequireHealthyBeforeStart(resource); }
        // Azurite is the cloud topology's storage account: the silo runs Topology=cloud over it (03-12), and the emulator
        // connection string names its blob, queue and table endpoints. There is no Key Vault emulator, so a vault the AppHost
        // configuration names is passed through; without one the silo stops naming IntoChatOS:Cloud:KeyVault.
        brain.AddProjection(new CloudTopology(grainState.Resource, HostConfiguration.HostKey(builder.Configuration, IntoChatOSNames.CloudKeyVaultConfigurationKey)));
    }

    private sealed class GrainStateContainer(string container) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(GrainStateContainerKey), container);
    }

    private sealed class CloudTopology(IResourceWithConnectionString account, string? keyVault) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey), IntoChatOSNames.TopologyCloud);
            builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudStorageConfigurationKey), account.ConnectionStringExpression);
            if (!string.IsNullOrWhiteSpace(keyVault))
            { builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.CloudKeyVaultConfigurationKey), keyVault); }
        }
    }
}
