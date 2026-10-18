using DigitalBrain.Aspire.Server;
using DigitalBrain.OS;
using DigitalBrain.Authority.Hosted;

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
