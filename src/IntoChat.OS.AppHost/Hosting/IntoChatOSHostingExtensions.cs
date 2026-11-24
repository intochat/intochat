using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Devices.Aspire.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Aspire.Hosting;

public static class IntoChatOSHostingExtensions
{
    private const string OrleansResourceSuffix = "intochat-os";

    static IntoChatOSHostingExtensions() => InitializeConfigurationRoot();

#pragma warning disable CA2255
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void InitializeConfigurationRoot() => ModuleConfigurationOptions.Current = new() { Root = "IntoChatOS" };

    // A vendor credential the host can run without. An empty value is not seeded, so the integration
    // stays unconfigured instead of the whole operating system refusing to start.
    public static IResourceBuilder<ParameterResource> AddOptionalParameter(this IDistributedApplicationBuilder builder, string name, bool secret = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return builder.AddParameter(name, () =>
        {
            var configured = builder.Configuration[$"Parameters:{name}"];
            return string.IsNullOrEmpty(configured) ? "" : configured;
        }, secret);
    }

    public static IResourceBuilder<AzureBlobStorageContainerResource> AddBlobContainer(this IntoChatOSBuilder brain, string name)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return (brain.Storage ?? throw new InvalidOperationException("Blob containers require the orleans module hosted on Azurite (OrleansModuleHosting with WithAzurite)."))
            .AddBlobContainer(brain.ResourceName(name), blobContainerName: name);
    }

    public static IntoChatOSBuilder AddIntoChatOSBrain(this IDistributedApplicationBuilder builder, string name, IntoChatOSHostingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        var resolvedServiceId = options.ServiceId ?? builder.Configuration[IntoChatOSHostingOptions.ServiceIdKey] ?? name;
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedServiceId);
        var environment = builder.Configuration[IntoChatOSHostingOptions.EnvironmentKey] is { Length: > 0 } configured ? configured : options.Environment;

        var resource = builder.AddResource(new IntoChatOSResource(ResourceNames.RootFor(builder, name)))
            .ExcludeFromManifest()
            .WithIconName(ResourceNames.RootIcon)
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "IntoChat",
                CreationTimeStamp = DateTime.UtcNow,
                State = KnownResourceStates.Running,
                Properties = [new(CustomResourceKnownProperties.Source, "IntoChat components")],
            })
            .WithCommand("reset-state", "Reset Local State", async _ =>
            {
                var code = await LocalReset.RunFromAppHostAsync(builder, ["--yes"]);
                return code == 0 ? CommandResults.Success() : CommandResults.Failure("Local reset failed.");
            });
        // Cluster membership may change between runs; persistence identity must not.
        var clusterId = options.ClusterId ?? builder.Configuration[IntoChatOSHostingOptions.ClusterIdKey]
            ?? (builder.Environment.IsDevelopment() ? $"os-{Guid.NewGuid():N}" : resolvedServiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        var grainStateContainer = options.GrainStateContainer ?? builder.Configuration[IntoChatOSHostingOptions.GrainStateContainerKey] ?? IntoChatOSNames.GrainStateContainer;
        // Storage is the orleans module's hosting choice (OrleansModuleHosting); without it the silo runs in memory.
        var orleans = builder
            .AddOrleans($"{name}-{OrleansResourceSuffix}")
            .WithClusterId(clusterId)
            .WithServiceId(resolvedServiceId)
            .WithDevelopmentClustering();
        var brain = new IntoChatOSBuilder(builder, name, resource, orleans) { ServiceId = resolvedServiceId, ClusterId = clusterId, GrainStateContainer = grainStateContainer, Environment = environment };
        brain.AddProjection(MasterKey.Provision(builder, name));
        if (AuthPosture.Provision(builder) is { } posture) { brain.AddProjection(posture); }
        return brain;
    }

    public static IResourceBuilder<TResource> WithReference<TResource>(this IResourceBuilder<TResource> builder, IntoChatOSBuilder brain)
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
            // No OS host is an Orleans cluster client: the kernel opens no gateway, whatever the Orleans reference allocates.
            context.EnvironmentVariables.Remove(RuntimeHosting.OrleansGatewayPortVariable);
        });
        // The Orleans reference gives every silo a gateway endpoint; an OS kernel keeps its silo endpoint only.
        foreach (var gateway in builder.Resource.Annotations.OfType<EndpointAnnotation>().Where(static e => e.Name == RuntimeHosting.OrleansGatewayEndpoint).ToArray())
        { builder.Resource.Annotations.Remove(gateway); }
        // Development clustering advertises localhost. Local executables must bind that address
        // too, rather than Orleans selecting a VPN/container adapter address.
        if (brain.Clustering is null && builder.Resource is ProjectResource or ExecutableResource or global::Aspire.Hosting.Dotnet.DotnetProjectResource)
        {
            builder.WithEnvironment("Orleans__Endpoints__AdvertisedIPAddress", "127.0.0.1");
            // Orleans system-target addresses carry the real port; a TCP proxy port cannot
            // substitute for it when a static client requests the cluster manifest.
            builder.WithEndpoint("orleans-silo", endpoint => endpoint.IsProxied = false);
        }
        // Storage, clustering and reminders reach the silo only through the orleans integration; the platform's
        // keyed blob client comes from it too, so the silo takes no storage connection strings.
        brain.Identity?.Apply(builder);
        if (brain.Environment is { Length: > 0 } environment)
        {
            builder.WithEnvironment("ASPNETCORE_ENVIRONMENT", environment);
            builder.WithEnvironment("DOTNET_ENVIRONMENT", environment);
        }

        foreach (var pair in brain.Settings)
        { builder.WithEnvironment(ModuleConfiguration.EnvironmentName(pair.Key), pair.Value ?? ""); }

        foreach (var pair in brain.ApplicationBuilder.Configuration.GetSection(IntegrationSchema.SectionName).AsEnumerable())
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Key.Equals(IntegrationSchema.SectionName, StringComparison.OrdinalIgnoreCase))
            { continue; }
            builder.WithEnvironment(ModuleConfiguration.EnvironmentName(pair.Key), pair.Value);
        }
        builder.WithEnvironment(context =>
        {
            var origin = ModuleConfiguration.EnvironmentName(ModuleConfiguration.ConnectionsPublicOriginKey);
            if (!context.EnvironmentVariables.ContainsKey(origin))
            { context.EnvironmentVariables[origin] = builder.GetEndpoint("http"); }
        });

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

        if (brain.Profile is null || brain.Profile.UsesDevice)
        {
            builder.WithInstallModelCommand(brain);
        }

        return builder;
    }

    public static IResourceBuilder<ContainerResource> AddIntoChatOSContainer(
        this IDistributedApplicationBuilder builder,
        string name,
        string image = "ghcr.io/intochat/os",
        string tag = "latest",
        int? httpPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return builder.AddContainer(name, image, tag)
            .WithHttpEndpoint(port: httpPort, targetPort: 8080, name: "http")
            .WithHttpHealthCheck("/health", endpointName: "http");
    }

    public static IResourceBuilder<ContainerResource> WithBootstrapOwner(
        this IResourceBuilder<ContainerResource> builder,
        string username,
        string password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        return builder
            .WithEnvironment(ModuleConfiguration.EnvironmentName(AuthKeys.Username), username)
            .WithEnvironment(ModuleConfiguration.EnvironmentName(AuthKeys.Password), password);
    }

    public static IResourceBuilder<ContainerResource> WithTopology(
        this IResourceBuilder<ContainerResource> builder,
        string topology)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(topology);
        return builder.WithEnvironment(ModuleConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey), topology);
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
