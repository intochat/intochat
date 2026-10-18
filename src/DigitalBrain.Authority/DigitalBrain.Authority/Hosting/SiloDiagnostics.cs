using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Authority.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Authority.Hosting;

// Operator diagnostics for post-deploy smoke tests: which silo answered (address, cluster, revision, host, storage
// mode, ownership counters) and a durability canary kept beside the brain logs. Mapped only when a deployment
// configures a key from its vault; every call must present it, so nothing here is public.
public static class SiloDiagnostics
{
    public const string KeyConfiguration = "DigitalBrain:Diagnostics:Key";
    public const string KeyHeader = "X-DigitalBrain-Diagnostics";
    // Azure Container Apps names the running revision in this variable.
    public const string RevisionVariable = "CONTAINER_APP_REVISION";
    public const string SiloPath = "/_diagnostics/silo";
    public const string CanaryPath = "/_diagnostics/canary";
    public const int MaxCanaryLength = 256;
    private const string CanaryDirectory = "diagnostics";
    private const string CanaryFile = "canary.json";

    public static IServiceCollection AddSiloDiagnostics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.TryAddSingleton<IBrainOwnershipCounters, NoBrainOwnershipCounters>();
        services.TryAddSingleton<BrainOwnershipMetrics>();
        return services;
    }

    public static IEndpointRouteBuilder MapSiloDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var services = endpoints.ServiceProvider;
        // The observable counter lives as long as the host; a web host without the silo services has none.
        _ = services.GetService<BrainOwnershipMetrics>();
        var configuration = services.GetRequiredService<IConfiguration>();
        if (configuration[KeyConfiguration] is not { } key || string.IsNullOrWhiteSpace(key)) { return endpoints; }
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        var group = endpoints.MapGroup("/_diagnostics")
            .AddEndpointFilter(async (context, next) =>
            {
                var supplied = context.HttpContext.Request.Headers[KeyHeader];
                if (supplied.Count != 1 || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0] ?? ""))))
                { return Results.Unauthorized(); }
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            })
            .AllowAnonymous(); // The configured key above authenticates these opt-in endpoints.

        group.MapGet("/silo", (IServiceProvider provider) => Results.Ok(Report(provider, configuration)));
        group.MapGet("/canary", async (IOptions<BrainStoreOptions> store, CancellationToken ct) =>
        {
            var path = CanaryPathOf(store.Value);
            if (!File.Exists(path)) { return Results.NotFound(); }
            await using var stream = File.OpenRead(path);
            return Results.Ok(await JsonSerializer.DeserializeAsync<CanaryRecord>(stream, cancellationToken: ct));
        });
        group.MapPost("/canary", async (CanaryWrite write, IOptions<BrainStoreOptions> store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(write.Value) || write.Value.Length > MaxCanaryLength) { return Results.BadRequest(); }
            var record = new CanaryRecord(write.Value, DateTimeOffset.UtcNow, Revision(configuration), Environment.MachineName);
            var path = CanaryPathOf(store.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written aside and moved over, so a reader sees the previous canary or this one, never a torn file.
            var staging = path + ".tmp";
            await using (var stream = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, cancellationToken: ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
            return Results.Ok(record);
        });
        return endpoints;
    }

    private static SiloReport Report(IServiceProvider provider, IConfiguration configuration)
    {
        var store = provider.GetRequiredService<IOptions<BrainStoreOptions>>().Value;
        var hosted = provider.GetServices<IHostedStorageExtension>().Any();
        return new SiloReport(
            provider.GetService<ILocalSiloDetails>()?.SiloAddress.ToString(),
            provider.GetService<IOptions<ClusterOptions>>()?.Value.ClusterId,
            Revision(configuration),
            Environment.MachineName,
            new SiloStorage(hosted ? "hosted" : "local", store.WriteFormat.ToString()),
            (provider.GetService<IBrainOwnershipCounters>() ?? new NoBrainOwnershipCounters()).Snapshot());
    }

    private static string? Revision(IConfiguration configuration) => configuration[RevisionVariable] is { Length: > 0 } revision ? revision : null;

    private static string CanaryPathOf(BrainStoreOptions store) => Path.Combine(store.Directory, CanaryDirectory, CanaryFile);
}

public sealed record SiloReport(string? SiloAddress, string? ClusterId, string? Revision, string Hostname, SiloStorage Storage, BrainOwnershipCounts Ownership);

public sealed record SiloStorage(string Mode, string WriteFormat);

public sealed record CanaryWrite(string? Value);

public sealed record CanaryRecord(string Value, DateTimeOffset WrittenAt, string? Revision, string Hostname);
