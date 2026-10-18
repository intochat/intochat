using DigitalBrain.Aspire.Server;
using DigitalBrain.OS;
using DigitalBrain.Authority.Hosted;
using DigitalBrain.Microsoft.Azure;
using DigitalBrain.Microsoft.Azure.Install;

// The installer runs before any cluster exists: it ensures the Azure foundation and exits.
if (AzureInstaller.IsInstall(args))
{
    Environment.ExitCode = await AzureInstaller.Run(args, new ArmAzure(), OsCatalog.Modules, Console.Out, Console.Error, CancellationToken.None);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var webHostServices = builder.Services.Where(x => x.ServiceType == typeof(IHostedService)).ToArray();
// A distribution of the operating system brings branding, features and configuration; the composition is the OS's.
// A hosted-cloud allocation is configured through the Hosting section, and only then composes the hosted authority.
if (builder.Configuration.GetSection(HostedRuntimeOptions.SectionName).Exists()) { builder.AddHostedAuthority(); }
builder.AddDigitalBrainServer(server => server.UseOrleansDashboard()
    .AddOperatingSystem(OperatingSystem.IsWindows() ? OsCatalog.WithWindowsEdge() : OsCatalog.Modules));
if (HostedRecoveryHost.IsRequested(builder.Configuration))
{
    HostedRecoveryHost.Configure(builder, webHostServices);
    var recovery = builder.Build();
    HostedRecoveryHost.Map(recovery);
    recovery.Run();
    return;
}
var app = builder.Build();
app.MapDigitalBrainServer();
app.Run();
