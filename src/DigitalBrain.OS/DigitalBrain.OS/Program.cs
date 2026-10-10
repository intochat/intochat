using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Framework;
using DigitalBrain.OS;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity;
using DigitalBrain.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// IntoChat is a distribution of the operating system: the composition is the OS's, and what
// is IntoChat's own is branding, features and configuration.
builder.AddDigitalBrainServer(server => server.UseAzureStorage().UseOrleansDashboard().AddOperatingSystem());
builder.AddDigitalBrainPlatform();
builder.AddMachinePartLogWhenConfigured();
// Features (os and intochat apps) load from the folder DigitalBrain:Modules:apps:Options:Features
// names: the AppHost passes the repository's ./features, the image carries a copy.

var app = builder.Build();

app.UsePlatformHttp();
app.MapOrleansDashboard();
// /health, /ready and /alive (ServiceDefaults.HealthProbes): deployments and the YARP front probe them.
app.MapDefaultEndpoints();
app.MapDigitalBrainModules();
app.MapDigitalBrainPlatform();


app.Run();
