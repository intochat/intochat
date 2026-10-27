using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

// The installer runs before any cluster exists: it ensures the Azure foundation and exits.
if (AzureInstaller.IsInstall(args))
{
    Environment.ExitCode = await AzureInstaller.Run(args, new ArmAzure(), OsCatalog.Modules, Console.Out, Console.Error, CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
// A distribution of the operating system brings branding, features and configuration; the composition is the OS's.
builder.AddIntoChatOSServer(server => server.UseOrleansDashboard()
    .AddOperatingSystem(OperatingSystem.IsWindows() ? OsCatalog.WithWindowsEdge() : OsCatalog.Modules));
var app = builder.Build();
app.MapIntoChatOSServer();
app.Run();
