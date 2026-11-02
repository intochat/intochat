using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.AppHost;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

// The AppHost owns its deployment keys: the published ports, and the features folder it hands the OS
// (the apps module's Features option, under IntoChatOSConfiguration.ModuleOptionsSection).
const string UiPortKey = "IntoChatOS:Http:UiPort";
const string McpPortKey = "IntoChatOS:Http:McpPort";
var featuresKey = IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ModuleOptionsSection("apps") + ":Features";

var builder = DistributedApplication.CreateBuilder(args);
// Legacy product-name settings stop the app host outside Development (the app host has no AddServiceDefaults).
LegacyConfigurationGuard.Verify(builder.Configuration, builder.Environment);
// `reset` clears this checkout's local run state and exits before any resource is added (Development and run mode only).
if (args.Length > 0 && args[0] == "reset")
{
    var userSecretsId = Assembly.GetExecutingAssembly().GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
    var secretsDirectory = userSecretsId is null ? null : Path.GetDirectoryName(PathHelper.GetSecretsPathFromSecretsId(userSecretsId));
    return await new LocalReset(
        builder.Environment, builder.ExecutionContext.IsRunMode, Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..")),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DigitalBrain.Runtime.Orleans.StateHome.Resolve(builder.Configuration, DigitalBrain.Runtime.Orleans.RuntimeOptions.IntoChatCompatible).Path,
        builder.Configuration["AppHost:PathSha256"] ?? "",
        secretsDirectory, new RealResetFileSystem(), new RealDockerCli(), Console.In, Console.Out, !Console.IsInputRedirected)
        .RunAsync(args[1..], CancellationToken.None);
}
// IntoChatOS:Topology (unset = local) is the only axis: it picks the module and model composition from code (RunProfile),
// whose module defaults sit above the shipped appsettings and below environment and command line, so an explicit
// IntoChatOS:Modules:<id>:Enabled still wins. Azurite and the cloud keys exist only for Topology=cloud.
var defaultsAt = builder.Configuration.Sources.ToList()
    .FindLastIndex(source => source is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource) + 1;
// Resolve the composition from the custom file too, while keeping module defaults below its explicit settings.
if (builder.Configuration["IntoChatOS:ConfigFile"] is { Length: > 0 } configFile)
{ builder.Configuration.AddJsonFile(configFile, optional: false); }
var topology = RunProfile.Topology(builder.Configuration);
var cloud = topology == IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyCloud;
builder.Configuration.Sources.Insert(defaultsAt,
    new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = RunProfile.ModuleDefaults(topology) });
var brainProfile = RunProfile.ForTopology(topology, builder.Configuration[IntoChatOSHostingOptions.LocalModelKey]);
if (cloud) { IntoChat.OS.Modules.Microsoft.Orleans.OrleansModuleOptionsExtensions.WithAzurite(brainProfile.Orleans); }
var shellEnabled = builder.Configuration.GetValue(IntoChat.OS.Contracts.Configuration.IntoChatOSNames.ShellEnabledKey, true);
var intoChatOS = AppHostIntoChatOS.Add(builder, "Modules", brainProfile, IntoChatOSHostingOptions.Named(builder.Configuration, "os"))
    .WithHttpIdentity();
if (shellEnabled) { intoChatOS = intoChatOS.WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp()); }

// Cookie keys persist in the Azurite blob container on cloud; a local run keeps them in the default key ring.
var cookieProtection = cloud ? intoChatOS.AddBlobContainer(intoChatOS.Identity!.ProtectionContainerName) : null;

// The repository's ./features: os apps, loaded by the OS at boot.
var features = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "features"));

var uiPort = builder.Configuration.GetValue(UiPortKey, 5080);
var mcpPort = builder.Configuration.GetValue(McpPortKey, 5081);

// One runtime owns this deployment; devices are connections to external capabilities.
var workdir = AspireModuleHosting.WorkdirOf(builder);
// Local state lives under the Home (IntoChatOS:Home, or ~/.intochat); a test brain passes a temporary one (TestHome).
var home = DigitalBrain.Runtime.Orleans.StateHome.Resolve(builder.Configuration, DigitalBrain.Runtime.Orleans.RuntimeOptions.IntoChatCompatible).Path;
var silos = AspireModuleHosting.InstanceNames("OperatingSystem").Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "os", "IntoChat.OS.Kernel.dll") is { } live
        // A live build reads the features published with it (AddLiveBuild), not the working tree's.
        ? Primary(AllowLiveBuildOnNetwork(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS")))
        : Primary(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.IntoChat_OS_Kernel>(name))
            .WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(featuresKey), features))).ToArray();
var osFront = AspireModuleHosting.AddFront(builder, "OperatingSystem", uiPort, silos.Select(silo => silo.GetEndpoint("https")));

// The front keeps a stable TLS endpoint across a single-runtime restart.
intoChatOS.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(osFront.Resource));

var mcps = AspireModuleHosting.InstanceNames("intochat-os-mcp").Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "mcp", "IntoChat.OS.Mcp.dll") is { } live
        ? Mcp(AllowLiveBuildOnNetwork(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS")))
        : Mcp(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.IntoChat_OS_Mcp>(name)))).ToArray();
AspireModuleHosting.AddFront(builder, "intochat-os-mcp", mcpPort, mcps.Select(mcp => mcp.GetEndpoint("http")));

builder.Build().Run();
return 0;

// A live build is an executable the proxy reaches through a bare port (ASPNETCORE_HTTP_PORTS), which binds every interface; the
// loopback-only rule for Open posture cannot be met there, so the opt-in is explicit (live builds are the candidate pipeline's).
IResourceBuilder<T> AllowLiveBuildOnNetwork<T>(IResourceBuilder<T> live) where T : IResourceWithEnvironment
    => live.WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(
        IntoChat.OS.Connections.Identity.Configuration.AuthOptions.Key(nameof(IntoChat.OS.Connections.Identity.Configuration.AuthOptions.AllowOpenOnNetwork))), "true");

IResourceBuilder<IResourceWithEndpoints> Silo<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    // One encrypted local brain store for this deployment.
    silo.WithReference(intoChatOS)
        .WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyKey), topology)
        .WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(IntoChat.OS.Contracts.Configuration.IntoChatOSNames.HomeKey), home)
        .WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.ConnectionsPublicOriginKey), $"http://localhost:{uiPort}".ToString())
        .WithDisabledModules(builder.Configuration)
        .WithHttpHealthCheck("/health", endpointName: "http");
    if (cookieProtection is not null) { silo.WaitFor(cookieProtection); }
    foreach (var key in new[] { IntoChat.OS.Contracts.Configuration.IntoChatOSNames.CustodyConfigurationKey,
        IntoChat.OS.Contracts.Configuration.IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey,
        IntoChat.OS.Contracts.Configuration.IntoChatOSNames.BrainStorageWriteFormatConfigurationKey })
    {
        if (builder.Configuration[key] is { Length: > 0 } value)
        { silo.WithEnvironment(key.Replace(":", "__"), value); }
    }
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(silo.Resource);
}

// Allocate Orleans ports after the brain reference so isolated AppHosts can run side by side.
IResourceBuilder<IResourceWithEndpoints> Primary<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithArgs
{
    // TLS negotiates HTTP/2 for the native shell through YARP, while HTTP remains for health and sandbox callbacks.
    // Projects compose ASPNETCORE_URLS from endpoints; an explicit port variable would be overridden by it.
    silo.WithHttpsEndpoint(name: "https", env: silo.Resource is ProjectResource ? null : "ASPNETCORE_HTTPS_PORTS", isProxied: true);
#pragma warning disable ASPIRECERTIFICATES001
    silo.WithHttpsDeveloperCertificate();
#pragma warning restore ASPIRECERTIFICATES001
    var primary = Silo(silo);
    silo.WithAllocatedOrleansPorts();
    return primary;
}

// The MCP server is an Orleans client: it starts once every silo instance is up.
IResourceBuilder<IResourceWithEndpoints> Mcp<T>(IResourceBuilder<T> mcp) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    mcp.WithReference(intoChatOS.AsClient(shareHttpIdentity: true))
        .WithEnvironment(IntoChat.OS.Framework.Modules.Configuration.IntoChatOSConfiguration.EnvironmentName(IntoChat.OS.Contracts.Configuration.IntoChatOSNames.TopologyKey), topology)
        .WithDisabledModules(builder.Configuration)
        .WithHttpHealthCheck("/health", endpointName: "http");
    for (var index = 0; index < silos.Length; index++)
    {
        var silo = silos[index];
        mcp.WaitFor(silo);
        // The local topology owns its allocated Orleans ports, so the client needs those gateways explicitly.
        if (!cloud)
        {
            mcp.WithEnvironment($"Orleans__Clustering__Gateways__{index}", ReferenceExpression.Create(
                $"127.0.0.1:{silo.GetEndpoint(RuntimeHosting.OrleansGatewayEndpoint).Property(EndpointProperty.Port)}"));
        }
    }
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(mcp.Resource);
}
