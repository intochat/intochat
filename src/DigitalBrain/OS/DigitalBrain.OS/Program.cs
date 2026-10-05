using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Assistant;
using DigitalBrain.Compute;
using DigitalBrain.Framework;
using DigitalBrain.OS;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity;

var builder = WebApplication.CreateBuilder(args);
LegacyConfigurationGuard.Check(builder.Configuration);

// IntoChat is a distribution of the operating system: the composition is the OS's, and what
// is IntoChat's own is branding, shipped apps and configuration.
builder.AddDigitalBrainServer(server => server.UseAzureStorage().AddOperatingSystem());
builder.AddDigitalBrainPlatform();
// Vendors ship through configuration: DigitalBrain:Modules:apps:Shipped:{source}:Path names a
// directory of app folders and :Account the publisher account that owns them. The image carries
// no vendor content of its own.
builder.Services.AddShippedApps(builder.Configuration);

var app = builder.Build();

app.UsePlatformHttp();
app.MapDigitalBrainModules();
app.MapDigitalBrainPlatform();


app.Run();
