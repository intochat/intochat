using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Aspire.Server;
using DigitalBrain.OS;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Testing.E2E;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Entry point for the module runtime process that ModuleTestHost launches. It runs against the
// test assembly's dependency closure, so every selected module assembly is already resolvable.
var builder = WebApplication.CreateBuilder(args);
var referenceComposition = ModuleHostChannel.IsReferenceComposition(builder.Configuration);
if (referenceComposition)
{
    builder.Configuration[DigitalBrain.Platform.Identity.Configuration.IdentityHostOptions.Key(nameof(DigitalBrain.Platform.Identity.Configuration.IdentityHostOptions.CookieName))] ??= "digitalbrain.reference.session";
}
builder.AddDigitalBrainServer(server =>
{
    server.UseAzureStorage();
    if (referenceComposition)
    {
        // The reference composition IS the operating system; the catalog is the one list.
        server.AddOperatingSystem();
    }
    else
    {
        foreach (var type in ModuleHostChannel.ReadModules(builder.Configuration))
        {
            server.AddModule(ModuleHostChannel.CreateModule(type));
        }
    }
});
builder.AddDigitalBrainPlatform();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
app.UseCors();
app.UsePlatformHttp();
app.MapDigitalBrainModules();
app.MapDigitalBrainPlatform();
app.MapHealthChecks(ModuleHostEndpoints.Health);
app.MapGet(ModuleHostEndpoints.Process, () => Environment.ProcessId);
await app.RunAsync();
