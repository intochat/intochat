using DigitalBrain.Launcher;
using DigitalBrain.Launcher.Processes;
using DigitalBrain.Launcher.Proxy;
using DigitalBrain.Launcher.Revisions;
using DigitalBrain.Launcher.Storage;
using DigitalBrain.Launcher.Telemetry;

var builder = WebApplication.CreateBuilder(args);
var repository = builder.Configuration[$"{LauncherOptions.Section}:Repository"];
if (string.IsNullOrWhiteSpace(repository)) { throw new InvalidOperationException("Launcher:Repository must name the repository root."); }
builder.Configuration[$"{LauncherOptions.Section}:Repository"] = Path.GetFullPath(repository);
var options = builder.Configuration.GetSection(LauncherOptions.Section).Get<LauncherOptions>()!;
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
builder.Services.AddSingleton<IStorageCopier>(AzuriteStorageCopier.At(options.AzuriteBlobEndpoint));
builder.Services.AddSingleton(HealthProbe.Http(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }));
builder.Services.AddRevisionProxy(options.UiPort, options.McpPort);
builder.Services.AddSingleton<Revisions>();
builder.Services.AddSingleton<IAspireTelemetry, AspireCliTelemetry>();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenLocalhost(options.ApiPort);
    kestrel.ListenLocalhost(options.UiPort);
    kestrel.ListenLocalhost(options.McpPort);
});

var app = builder.Build();
// The API answers only on its own port; everything on 5080/5081 is proxied.
app.MapWhen(context => context.Connection.LocalPort == options.ApiPort, api => { api.UseRouting(); api.UseEndpoints(endpoints => endpoints.MapLauncher()); });
app.MapReverseProxy();
// A launcher restarted while a revision is current points at it again before serving.
if (app.Services.GetRequiredService<Revisions>().List() is { Current: { } current } document)
{
    var revision = document.Require(current);
    app.Services.GetRequiredService<RevisionProxy>().PointAt(revision.UiPort, revision.McpPort);
}
app.Run();
