using DigitalBrain.Aspire.Client;
using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Mcp;
using DigitalBrain.Authority.Identity;
using DigitalBrain.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddDigitalBrainClient(useAzureClustering: true);
builder.AddKeyedAzureBlobServiceClient(DigitalBrainNames.GrainState);
builder.Services.AddCookieProtection();
builder.Services.AddDigitalBrainMcp();

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAccountSession();
app.MapDefaultEndpoints();
app.MapDigitalBrainMcp();
app.Run();
