using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Sandbox;

if (args.Contains("--unit-host", StringComparer.Ordinal))
{
    string? contractsDir = null;
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--contracts-dir", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(args[i + 1]))
        {
            contractsDir = args[i + 1];
            break;
        }
    }

    var exitCode = await UnitHostWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), contractsDir).ConfigureAwait(false);
    Environment.Exit(exitCode);
}

var builder = WebApplication.CreateBuilder(args);

var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrWhiteSpace(configuredUrls))
{
    var port = Environment.GetEnvironmentVariable("PORT");
    var listenUrl = int.TryParse(port, out var portNumber) && portNumber > 0
        ? $"http://0.0.0.0:{portNumber}"
        : "http://0.0.0.0:8080";
    builder.WebHost.UseUrls(listenUrl);
}

var kernelUrl = builder.Configuration["SANDBOX_KERNEL_URL"];
builder.Services.AddHttpClient("SandboxKernel", client =>
{
    if (!string.IsNullOrWhiteSpace(kernelUrl)
        && Uri.TryCreate(kernelUrl, UriKind.Absolute, out var baseUri))
    {
        client.BaseAddress = baseUri;
    }
});

builder.Services.AddSingleton(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<AppSandboxHost>>();
    return new AppSandboxHost(factory.CreateClient("SandboxKernel"), config, logger);
});

var app = builder.Build();

static IResult HealthStatus(AppSandboxHost host)
    => Results.Ok(new { status = "ok", loadedApps = host.LoadedAppCount });

app.MapGet("/health", HealthStatus);
app.MapGet("/alive", HealthStatus);
app.MapGet("/ready", HealthStatus);

var enrollKey = app.Configuration["SANDBOX_ENROLL_KEY"];
var enrollKeyBytes = string.IsNullOrEmpty(enrollKey) ? null : Encoding.UTF8.GetBytes(enrollKey);

var apps = app.MapGroup("/apps");
if (enrollKeyBytes is not null)
{
    apps.AddEndpointFilter(async (context, next) =>
    {
        if (!context.HttpContext.Request.Headers.TryGetValue("X-Sandbox-Enroll-Key", out var provided)
            || provided.Count != 1)
        {
            return Results.Unauthorized();
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided[0] ?? string.Empty);
        if (providedBytes.Length != enrollKeyBytes.Length
            || !CryptographicOperations.FixedTimeEquals(providedBytes, enrollKeyBytes))
        {
            return Results.Unauthorized();
        }

        return await next(context).ConfigureAwait(false);
    });
}

apps.MapPost("/deploy", async (DeployAppRequest request, AppSandboxHost host, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BrainId)
        || string.IsNullOrWhiteSpace(request.AppKey)
        || string.IsNullOrWhiteSpace(request.AssemblyBase64))
    {
        return Results.BadRequest(new { error = "BrainId, AppKey, and AssemblyBase64 are required." });
    }

    var response = await host.DeployAsync(request, cancellationToken).ConfigureAwait(false);
    return Results.Ok(response);
});

apps.MapPost("/dispatch", async (DispatchAppRequest request, AppSandboxHost host, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BrainId)
        || string.IsNullOrWhiteSpace(request.AppKey)
        || string.IsNullOrWhiteSpace(request.Kind))
    {
        return Results.BadRequest(new { error = "BrainId, AppKey, and Kind are required." });
    }

    try
    {
        var response = await host.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(response);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

apps.MapPost("/unload", async (UnloadAppRequest request, AppSandboxHost host, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.BrainId) || string.IsNullOrWhiteSpace(request.AppKey))
    {
        return Results.BadRequest(new { error = "BrainId and AppKey are required." });
    }

    var response = await host.UnloadAsync(request.BrainId, request.AppKey, cancellationToken).ConfigureAwait(false);
    return Results.Ok(response);
});

await app.RunAsync().ConfigureAwait(false);
