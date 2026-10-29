using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Kernel;
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
builder.AddIntoChatOSServer(server =>
{
    server.UseOrleansDashboard().AddOperatingSystem(catalog);
    if (!CloudHost.IsCloud(topology)) { return; }
    server.UseLicenseCheck(new CloudLicenseCheck()).ConfigureServices(CloudHost.Register);
});
var app = builder.Build();
app.MapIntoChatOSServer();
app.Run();
