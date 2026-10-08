using DigitalBrain;
using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Aspire.Server;
using DigitalBrain.Compute;
using DigitalBrain.Framework;
using DigitalBrain.OS;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity;

var builder = WebApplication.CreateBuilder(args);

// IntoChat is a distribution of the operating system: the composition is the OS's, and what
// is IntoChat's own is branding, shipped apps and configuration.
builder.AddDigitalBrainServer(server => server.UseAzureStorage().UseOrleansDashboard().AddOperatingSystem());
builder.AddDigitalBrainPlatform();
// Vendors ship through configuration: DigitalBrain:Modules:apps:Shipped:{vendor}:Path names a
// directory of app folders listed under that vendor. The image carries no vendor content of its
// own; in development the folders are writable so Save to source can write edits back.
builder.Services.AddShippedApps(builder.Configuration, development: builder.Environment.IsDevelopment());

var app = builder.Build();

app.UsePlatformHttp();
app.MapOrleansDashboard();
app.MapDigitalBrainModules();
app.MapDigitalBrainPlatform();


app.Run();
