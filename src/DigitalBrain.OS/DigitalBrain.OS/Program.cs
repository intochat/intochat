using DigitalBrain.Aspire.Server;
using DigitalBrain.OS;
using DigitalBrain.Authority.Hosting;

var builder = WebApplication.CreateBuilder(args);
var webHostServices = builder.Services.Where(x => x.ServiceType == typeof(IHostedService)).ToArray();
// IntoChat is a distribution of the operating system: the composition is the OS's, and what is
// IntoChat's own is branding, features and configuration (the AppHost names the ./features folder).
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
