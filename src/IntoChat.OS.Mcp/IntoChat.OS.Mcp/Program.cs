using IntoChat.OS.Aspire.Client;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Mcp;
using IntoChat.OS.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
var cloud = string.Equals(builder.Configuration[IntoChatOSNames.TopologyKey], IntoChatOSNames.TopologyCloud, StringComparison.OrdinalIgnoreCase);
builder.AddIntoChatOSClient(useAzureClustering: cloud);
if (cloud) { builder.AddKeyedAzureBlobServiceClient(IntoChatOSNames.GrainState); }
builder.Services.AddCookieProtection();
builder.Services.AddIntoChatOSMcp();

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAccountSession();
app.MapDefaultEndpoints();
app.MapIntoChatOSMcp();
app.Run();
