using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Aspire.Hosting;

public static class DigitalBrainHostingExtensions
{
    private const string OrleansResourceSuffix = "digitalbrain";

    public static IResourceBuilder<AzureBlobStorageContainerResource> AddBlobContainer(this DigitalBrainBuilder brain, string name)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return (brain.Storage ?? throw new InvalidOperationException("Blob containers require the orleans module hosted on Azurite (OrleansModuleHosting with WithAzurite)."))
            .AddBlobContainer(brain.ResourceName(name), blobContainerName: name);
    }

    public static DigitalBrainBuilder AddDigitalBrain(this IDistributedApplicationBuilder builder, string name, DigitalBrainHostingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        var resolvedServiceId = options.ServiceId ?? builder.Configuration[DigitalBrainHostingOptions.ServiceIdKey] ?? name;
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedServiceId);
        var environment = builder.Configuration[DigitalBrainHostingOptions.EnvironmentKey] is { Length: > 0 } configured ? configured : options.Environment;

        var resource = builder.AddResource(new DigitalBrainResource(name))
            .ExcludeFromManifest()
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Modules",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running,
                Properties = [new(CustomResourceKnownProperties.Source, "DigitalBrain modules")],
            });
        // Cluster membership may change between runs; persistence identity must not.
        var clusterId = options.ClusterId ?? builder.Configuration[DigitalBrainHostingOptions.ClusterIdKey]
            ?? (builder.Environment.IsDevelopment() ? $"digitalbrain-{Guid.NewGuid():N}" : resolvedServiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        var grainStateContainer = options.GrainStateContainer ?? builder.Configuration[DigitalBrainHostingOptions.GrainStateContainerKey] ?? DigitalBrainNames.GrainStateContainer;
        // Storage is the orleans module's hosting choice (OrleansModuleHosting); without it the silo runs in memory.
        var orleans = builder
            .AddOrleans($"{name}-{OrleansResourceSuffix}")
            .WithClusterId(clusterId)
            .WithServiceId(resolvedServiceId)
            .WithDevelopmentClustering();
        var brain = new DigitalBrainBuilder(builder, name, resource, orleans) { ServiceId = resolvedServiceId, ClusterId = clusterId, GrainStateContainer = grainStateContainer, Environment = environment };
        brain.AddProjection(MasterKey.Provision(builder, name));
        if (AuthPosture.Provision(builder) is { } posture) { brain.AddProjection(posture); }
        return brain;
    }

    public static IResourceBuilder<TResource> WithReference<TResource>(this IResourceBuilder<TResource> builder, DigitalBrainBuilder brain)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(brain);

        builder.WithAnnotation(new BrainSiloAnnotation(brain));
        builder.WithReference(brain.Orleans);
        // The silo keeps the identity and endpoint keys; its providers come from the orleans module, so the
        // development clustering Aspire requires on the Orleans resource must not reach it.
        builder.WithEnvironment(context =>
        {
            foreach (var key in context.EnvironmentVariables.Keys.Where(IsOrleansProviderKey).ToArray())
            { context.EnvironmentVariables.Remove(key); }
        });
        // Development clustering advertises localhost gateways. Local executables must bind
        // that address too, rather than Orleans selecting a VPN/container adapter address.
        if (brain.Clustering is null && builder.Resource is ProjectResource or ExecutableResource)
        {
            builder.WithEnvironment("Orleans__Endpoints__AdvertisedIPAddress", "127.0.0.1");
            // Orleans system-target addresses carry the real port; a TCP proxy port cannot
            // substitute for it when a static client requests the cluster manifest.
            builder.WithEndpoint("orleans-silo", endpoint => endpoint.IsProxied = false);
            builder.WithEndpoint("orleans-gateway", endpoint => endpoint.IsProxied = false);
        }
        // The connection strings stay for the keyed clients the host resolves (cookie protection, deployment
        // identity); Orleans storage itself comes from the orleans integration, not Orleans__* keys.
        if (brain.Clustering is { } clustering) { builder.WithReference(clustering, DigitalBrainNames.Clustering); }
        if (brain.Reminders is { } reminders) { builder.WithReference(reminders, DigitalBrainNames.Reminders); }
        if (brain.GrainState is { } grainState) { builder.WithReference(grainState, DigitalBrainNames.GrainState); }
        brain.Identity?.Apply(builder);
        if (brain.Environment is { Length: > 0 } environment)
        {
            builder.WithEnvironment("ASPNETCORE_ENVIRONMENT", environment);
            builder.WithEnvironment("DOTNET_ENVIRONMENT", environment);
        }

        foreach (var pair in brain.Settings)
        { builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(pair.Key), pair.Value ?? ""); }

        // Aspire's OTLP injection prefers gRPC; a host that names only an HTTP collector gets http/protobuf.
        // Run mode only: a published manifest never carries the local dashboard's collector URL.
        var configuration = builder.ApplicationBuilder.Configuration;
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode
            && string.IsNullOrEmpty(configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"])
            && configuration["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] is { Length: > 0 } http)
        {
            builder.WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", http);
            builder.WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        }

        WaitUntilHealthy(builder, brain.StartupDependencies);

        foreach (var projection in brain.Projections)
        {
            projection.Apply(builder);
        }

        return builder;
    }

    public static IResourceBuilder<TResource> WithReference<TResource>(this IResourceBuilder<TResource> builder, DigitalBrainClientReference client)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);

        builder.WithReference(client.Brain.Orleans.AsClient());
        AuthPosture.Provision(client.Brain.ApplicationBuilder)?.Apply(builder);
        if (client.ShareHttpIdentity)
        {
            var identity = client.Brain.Identity ?? throw new InvalidOperationException("Configure the brain HTTP identity before sharing sessions.");
            var storage = client.Brain.GrainState ?? throw new InvalidOperationException("Shared HTTP sessions require durable key storage.");
            identity.Apply(builder);
            builder.WithReference(storage, DigitalBrainNames.GrainState);
        }
        if (client.Brain.Clustering is { } clustering)
        {
            builder.WithReference(clustering, DigitalBrainNames.Clustering);
            // Overrides the development provider the Orleans resource advertises.
            builder.WithEnvironment("Orleans__Clustering__ProviderType", "AzureTableStorage");
            builder.WithEnvironment("Orleans__Clustering__ServiceKey", DigitalBrainNames.Clustering);
        }
        WaitUntilHealthy(builder, client.Brain.StartupDependencies);
        return builder;
    }

    private static bool IsOrleansProviderKey(string key)
        => key.StartsWith("Orleans__Clustering__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__Reminders__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__GrainStorage__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__Streaming__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__GrainDirectory__", StringComparison.Ordinal)
            || key.StartsWith("Orleans__BroadcastChannel__", StringComparison.Ordinal);

    private static void WaitUntilHealthy<TResource>(
        IResourceBuilder<TResource> builder,
        IReadOnlyList<IResource> dependencies)
        where TResource : IResourceWithEnvironment, IResourceWithEndpoints
    {
        foreach (var dependency in dependencies)
        {
            builder.WithAnnotation(new WaitAnnotation(dependency, WaitType.WaitUntilHealthy, exitCode: 0));
        }
    }
}
