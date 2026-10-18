using IntoChat.OS.Aspire.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Mcp;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddIntoChatOSClient(useAzureClustering: true);
builder.AddKeyedAzureBlobServiceClient(IntoChatOSNames.GrainState);
builder.Services.AddCookieProtection();
builder.Services.AddIntoChatOSMcp();

var app = builder.Build();
app.UseRouting();
app.UseAuthentication();
app.UseAccountSession();
app.MapDefaultEndpoints();
app.MapIntoChatOSMcp();
app.Run();
