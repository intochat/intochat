using DigitalBrain.Aspire.Server;
using DigitalBrain.OS;

var builder = WebApplication.CreateBuilder(args);
// IntoChat is a distribution of the operating system: the composition is the OS's, and what is
// IntoChat's own is branding, features and configuration (the AppHost names the ./features folder).
builder.AddDigitalBrainServer(server => server.UseOrleansDashboard().AddOperatingSystem());
var app = builder.Build();
app.MapDigitalBrainServer();
app.Run();
