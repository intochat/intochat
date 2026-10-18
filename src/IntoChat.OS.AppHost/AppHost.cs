using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.AppHost;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Aspire.Hosting;
using IntoChat.OS.Microsoft.Aspire;
using IntoChat.OS.Microsoft.CSharp;
using Microsoft.Extensions.Configuration;

// The AppHost owns its deployment keys: the published ports, and the features folder it hands the OS
// (the apps module's Features option, under IntoChatOSConfiguration.ModuleOptionsSection).
const string UiPortKey = "DigitalBrain:Http:UiPort";
const string McpPortKey = "DigitalBrain:Http:McpPort";
var featuresKey = IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.ModuleOptionsSection("apps") + ":Features";

var builder = DistributedApplication.CreateBuilder(args);
// The profile symbol in this file is the selection: RunProfile.Cloud or RunProfile.Local.
builder.Configuration.AddIntoChatOSProfiles(builder.AppHostDirectory);
if (builder.Configuration["DigitalBrain:ConfigFile"] is { Length: > 0 } configFile)
{ builder.Configuration.AddJsonFile(configFile, optional: false); }
var shellEnabled = builder.Configuration.GetValue("DigitalBrain:Shell:Enabled", true);
var intoChatOS = AppHostIntoChatOS.Add(builder, "Modules", RunProfile.Cloud, IntoChatOSHostingOptions.Named(builder.Configuration, "intochat"))
    .WithHttpIdentity();
if (shellEnabled) { intoChatOS = intoChatOS.WithHosting<FlutterModuleHosting, FlutterModuleOptions>(flutter => flutter.RunDesktopApp()); }

var cookieProtection = intoChatOS.AddBlobContainer(intoChatOS.Identity!.ProtectionContainerName);

// The repository's ./features: os apps, loaded by the OS at boot.
var features = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "features"));

var uiPort = builder.Configuration.GetValue(UiPortKey, 5080);
var mcpPort = builder.Configuration.GetValue(McpPortKey, 5081);

// One runtime owns this deployment; devices are connections to external capabilities.
var workdir = AspireModuleHosting.WorkdirOf(builder);
var storageDirectory = builder.Configuration["DigitalBrain:Platform:Storage:Directory"]
    ?? (builder.Configuration.GetValue(IntoChatOSHostingOptions.PersistentStorageKey, true)
        ? Path.Combine(workdir, "brain-store")
        : Path.Combine(workdir, "test-brains", intoChatOS.ClusterId));
var silos = AspireModuleHosting.InstanceNames("OperatingSystem").Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "os", "IntoChat.OS.Kernel.dll") is { } live
        // A live build reads the features published with it (AddLiveBuild), not the working tree's.
        ? Primary(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS"))
        : Primary(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.IntoChat_OS_Kernel>(name))
            .WithEnvironment(IntoChat.OS.Framework.Configuration.IntoChatOSConfiguration.EnvironmentName(featuresKey), features))).ToArray();
var osFront = AspireModuleHosting.AddFront(builder, "OperatingSystem", uiPort, silos.Select(silo => silo.GetEndpoint("https")));

// The front keeps a stable TLS endpoint across a single-runtime restart.
intoChatOS.WithShellFront(builder.CreateResourceBuilder<IResourceWithEndpoints>(osFront.Resource));

var mcps = AspireModuleHosting.InstanceNames("intochat-os-mcp").Select(name =>
    AspireModuleHosting.AddLiveBuild(builder, workdir, name, "mcp", "IntoChat.OS.Mcp.dll") is { } live
        ? Mcp(AspireModuleHosting.OnDynamicPorts(live, "ASPNETCORE_HTTP_PORTS"))
        : Mcp(AspireModuleHosting.OnDynamicPorts(builder.AddProject<Projects.IntoChat_OS_Mcp>(name)))).ToArray();
AspireModuleHosting.AddFront(builder, "intochat-os-mcp", mcpPort, mcps.Select(mcp => mcp.GetEndpoint("http")));

builder.Build().Run();

IResourceBuilder<IResourceWithEndpoints> Silo<T>(IResourceBuilder<T> silo) where T : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithWaitSupport
{
    // One encrypted local brain store for this deployment.
    silo.WithReference(intoChatOS)
        .WithEnvironment("DigitalBrain__Platform__Storage__Directory", storageDirectory)
        .WaitFor(cookieProtection)
        .WithHttpHealthCheck("/health", endpointName: "http");
    foreach (var key in new[] { IntoChat.OS.Framework.IntoChatOSNames.CustodyConfigurationKey,
        IntoChat.OS.Framework.IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey,
        IntoChat.OS.Framework.IntoChatOSNames.BrainStorageWriteFormatConfigurationKey })
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
        .WithHttpHealthCheck("/health", endpointName: "http");
    foreach (var silo in silos) { mcp.WaitFor(silo); }
    return builder.CreateResourceBuilder<IResourceWithEndpoints>(mcp.Resource);
}
