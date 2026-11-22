using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Cluster;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Devices.Aspire.Hosting;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.Microsoft.Orleans;

namespace IntoChat.OS.Aspire.Hosting;

// The developer deployment's resources: the kernel (one runtime per instance), the MCP host, their fronts and the shell.
public static class OperatingSystemHosting
{
    // Settings the kernel reads that only the AppHost's configuration may carry; forwarded when set.
    private static readonly string[] ForwardedKernelKeys =
    [
        IntoChatOSNames.CustodyConfigurationKey,
        IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey,
        IntoChatOSNames.BrainStorageWriteFormatConfigurationKey,
        "IntoChatOS:AspireDashboardUrl",
        "ASPIRE_DASHBOARD_URL",
        "DOTNET_DASHBOARD_URLS",
    ];

    public static IntoChatOSBuilder AddIntoChatOS(
        this IDistributedApplicationBuilder builder,
        string name,
        IntoChatOSHostingOptions? hosting = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var profile = DeploymentProfile.From(builder);
        var os = builder.AddIntoChatOSBrain(name, hosting ?? IntoChatOSHostingOptions.Named(builder.Configuration, "os"));
        os.Profile = profile;
        os = os
            .WithHosting<OrleansModuleHosting, OrleansOptions>(profile.Orleans)
            .WithHosting<AspireModuleHosting>()
            .WithHttpIdentity();
        return profile.ShellEnabled
            ? os.WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp())
            : os;
    }

    public static IResourceBuilder<IResourceWithEndpoints>[] AddKernel(this IntoChatOSBuilder os, string projectPath)
    {
        ArgumentNullException.ThrowIfNull(os);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var profile = os.Profile
            ?? throw new InvalidOperationException("AddKernel needs the deployment from builder.AddIntoChatOS(name).");

        var builder = os.ApplicationBuilder;
        // Cookie keys persist in the Azurite blob container on cloud; a local run keeps them in the default key ring.
        var cookieProtection = profile.Cloud ? os.AddBlobContainer(os.Identity!.ProtectionContainerName) : null;
        var silos = AspireModuleHosting.InstanceNames("OperatingSystem").Select(name =>
            Primary(AspireModuleHosting.OnDynamicPorts(builder.AddDotnetProject(name, projectPath)))).ToArray();
        if (profile.FrontEnabled)
        {
            var front = AspireModuleHosting.AddFront(builder, "OperatingSystem", profile.UiPort, silos.Select(silo => silo.GetEndpoint("https")));
            os.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(front.Resource));
        }
        return silos;

        // Allocate Orleans ports after the brain reference so isolated AppHosts can run side by side.
        IResourceBuilder<IResourceWithEndpoints> Primary<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithArgs
        {
            // TLS negotiates HTTP/2 for the native shell through YARP, while HTTP remains for health and sandbox callbacks.
            // Projects compose ASPNETCORE_URLS from endpoints; an explicit port variable would be overridden by it.
            silo.WithHttpsEndpoint(name: "https", env: silo.Resource is ProjectResource or DotnetProjectResource ? null : "ASPNETCORE_HTTPS_PORTS", isProxied: true);
#pragma warning disable ASPIRECERTIFICATES001
            silo.WithHttpsDeveloperCertificate();
#pragma warning restore ASPIRECERTIFICATES001
            // One click from the dashboard to DevUI, Widgetbook, and Orleans Dashboard on the kernel's loopback endpoint.
            silo.WithUrlForEndpoint("http", static _ => new ResourceUrlAnnotation { Url = "/devui/", DisplayText = "DevUI" });
            silo.WithUrlForEndpoint("http", static _ => new ResourceUrlAnnotation { Url = "/widgetbook/", DisplayText = "Widgetbook" });
            silo.WithUrlForEndpoint("http", static _ => new ResourceUrlAnnotation { Url = "/orleans/", DisplayText = "Orleans Dashboard" });
            // A local kernel may read this AppHost through Aspire's own MCP server (Ino's IAspire) and inspect this checkout as the "os" repository; a cloud one never learns where it is.
            if (!profile.Cloud)
            {
                silo.WithEnvironment("IntoChatOS__Aspire__AppHostPath", Path.Combine(builder.AppHostDirectory, "IntoChat.OS.AppHost.csproj"));
                ProjectLocalCheckout(silo, builder);
            }
            // One encrypted local brain store for this deployment.
            silo.WithReference(os)
                .WithOsEnvironment(IntoChatOSNames.TopologyKey, profile.Topology)
                .WithOsEnvironment(IntoChatOSNames.HomeKey, profile.Home)
                .WithOsEnvironment(ModuleConfiguration.ConnectionsPublicOriginKey, $"http://localhost:{profile.UiPort}")
                .WithModuleOverrides(builder, profile)
                .WithHttpHealthCheck("/health", endpointName: "http");
            if (profile.UsesDevice)
            {
                silo.WithInstallModelCommand(os);
            }
            if (cookieProtection is not null) { silo.WaitFor(cookieProtection); }
            foreach (var key in ForwardedKernelKeys)
            {
                if (builder.Configuration[key] is { Length: > 0 } value) { silo.WithEnvironment(key.Replace(":", "__"), value); }
            }
            silo.WithAllocatedOrleansPorts();
            return builder.CreateResourceBuilder<IResourceWithEndpoints>(silo.Resource);
        }
    }

    /// <summary>
    /// Adds the unmodified IntoChat OS container (<c>ghcr.io/intochat/os</c>) to the cluster so external hosts
    /// or Dockerized apps can run alongside the containerized OS kernel without needing the <c>intochat</c> source tree.
    /// </summary>
    public static IResourceBuilder<ContainerResource> AddKernelContainer(
        this IntoChatOSBuilder os,
        string image = "ghcr.io/intochat/os",
        string tag = "latest",
        string name = "OperatingSystem-container")
    {
        ArgumentNullException.ThrowIfNull(os);
        var profile = os.Profile
            ?? throw new InvalidOperationException("AddKernelContainer needs the deployment from builder.AddIntoChatOS(name).");

        var builder = os.ApplicationBuilder;
        var cookieProtection = profile.Cloud ? os.AddBlobContainer(os.Identity!.ProtectionContainerName) : null;
        var container = builder.AddIntoChatOSContainer(name, image, tag)
            .WithReference(os)
            .WithOsEnvironment(IntoChatOSNames.TopologyKey, profile.Topology)
            .WithOsEnvironment(IntoChatOSNames.HomeKey, "/data/home")
            .WithOsEnvironment(ModuleConfiguration.ConnectionsPublicOriginKey, $"http://localhost:{profile.UiPort}")
            .WithModuleOverrides(builder, profile)
            .WithAllocatedOrleansPorts();
        if (cookieProtection is not null) { container.WaitFor(cookieProtection); }
        foreach (var key in ForwardedKernelKeys)
        {
            if (builder.Configuration[key] is { Length: > 0 } value) { container.WithEnvironment(key.Replace(":", "__"), value); }
        }
        return container;
    }

    private static IResourceBuilder<T> WithOsEnvironment<T>(this IResourceBuilder<T> resource, string key, string value) where T : IResourceWithEnvironment
        => resource.WithEnvironment(ModuleConfiguration.EnvironmentName(key), value);

    // The kernel and MCP apply the topology's module defaults themselves; only the AppHost's explicit overrides cross over.
    private static IResourceBuilder<T> WithModuleOverrides<T>(this IResourceBuilder<T> resource, IDistributedApplicationBuilder builder, DeploymentProfile profile) where T : IResourceWithEnvironment
    {
        foreach (var key in profile.ModuleOverrides) { resource.WithOsEnvironment(key, builder.Configuration[key]!); }
        return resource;
    }

    internal static void ProjectLocalCheckout<T>(IResourceBuilder<T> silo, IDistributedApplicationBuilder builder)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(builder);

        var osRepoSection = builder.Configuration.GetSection("IntoChatOS:Repositories:os");
        var alreadyConfigured = !string.IsNullOrWhiteSpace(osRepoSection.Value) || osRepoSection.GetChildren().Any();
        if (!alreadyConfigured && FindCheckoutRoot(builder.AppHostDirectory) is { Length: > 0 } checkoutRoot)
        {
            silo.WithEnvironment("IntoChatOS__Repositories__os", checkoutRoot);
        }
    }

    internal static string? FindCheckoutRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var gitMarker = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
