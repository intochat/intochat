using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

// The installer runs before any cluster exists: it ensures the Azure foundation and exits.
if (AzureInstaller.IsInstall(args))
{
    Environment.ExitCode = await AzureInstaller.Run(args, new ArmAzure(), OperatingSystemModules.Modules, Console.Out, Console.Error, CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
LocalRunDefaults.Apply(builder.Configuration);
// A distribution of the operating system brings branding, features and configuration; the composition is the OS's.
// Cloud modules declare LoadsUnder and stay out of a local topology.
var topology = builder.Configuration[IntoChatOSNames.TopologyKey];
var catalog = (OperatingSystem.IsWindows() ? OperatingSystemModules.WithWindowsEdge() : OperatingSystemModules.Modules)
    .Where(module => module.LoadsUnder(topology)).ToArray();
var dashboardEnabled = builder.Configuration.GetValue<bool>("OrleansDashboard:Enabled")
    || builder.Configuration.GetValue<bool>("IntoChatOS:OrleansDashboard:Enabled")
    || (builder.Environment.IsDevelopment() && !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]));
builder.AddIntoChatOSServer(server =>
{
    if (dashboardEnabled) { server.UseOrleansDashboard(); }
    server.AddOperatingSystem(catalog);
    if (!CloudHost.IsCloud(topology)) { return; }
    server.UseLicenseCheck(new CloudLicenseCheck()).ConfigureServices(CloudHost.Register);
});
builder.Services.AddIntoChatOSMcp();
var app = builder.Build();
app.MapIntoChatOSServer();
app.MapIntoChatOSMcp();
app.Run();
