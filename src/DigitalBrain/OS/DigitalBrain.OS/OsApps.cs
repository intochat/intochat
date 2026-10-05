using DigitalBrain.Apps;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace DigitalBrain.OS;

// The operating system's first-party apps: names every brain carries, imported from the "os"
// brain's shipped copy when the brain activates and re-imported when a new version ships.
// The list is deployment configuration; a distribution names its own.
public sealed record OsApp(string Name);

public sealed class OsAppsOptions
{
    public IList<OsApp> Apps { get; } = [];
}

public static class OsAppsHosting
{
    private sealed class OsAppsRegistered;

    // The operating system names and ships its own apps; a distribution's configure only adds.
    // Idempotent: the composition wires this itself, and a host calling it again is a no-op
    // apart from its own configure delegate.
    public static IServiceCollection AddOperatingSystemApps(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(OsAppsRegistered)))
        {
            if (configure is not null) { services.Configure(configure); }
            return services;
        }
        services.AddSingleton<OsAppsRegistered>();
        services.AddShippedApps(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", account: "os");
        // The OS's apps belong to its own account; every distribution carries it.
        services.Configure<DigitalBrain.Platform.Identity.Configuration.SeededAccountOptions>(seeded =>
        {
            if (!seeded.Accounts.Any(account => account.Username == "os"))
            { seeded.Accounts.Add(new() { Username = "os", DisplayName = "DigitalBrain OS" }); }
        });
        services.Configure<OsAppsOptions>(os =>
        {
            // Idempotent so a host that both composes the OS and wires the apps explicitly
            // does not boot each app twice.
            if (!os.Apps.Any(existing => existing.Name == "settings")) { os.Apps.Add(new("settings")); }
        });
        return services.AddOperatingSystemBoot(configure);
    }

    public static IServiceCollection AddOperatingSystemBoot(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null) { services.Configure(configure); }
        services.AddHostedService<OsBootService>();
        return services;
    }
}

// The OS boots in the system's own language: the brain neuron publishes Activated, and this
// behavior reacts by ensuring the OS apps are present and current — exporting the "os" brain's
// verified copy and importing it into the activated brain, the same path any shared app takes.
// Delivery is at-least-once and Activated repeats on every activation, so every step reads
// before it writes; an app that has not shipped yet is simply picked up on the next activation.
internal sealed class OsBootService(
    LocalSignalHub signals, IGrainFactory grains, IPublisherDirectory publishers, IOptions<OsAppsOptions> options, ILogger<OsBootService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Apps.Count == 0) { return; }
        var apps = options.Value.Apps.ToArray();
        using var activations = signals.Subscribe<Activated>();
        await foreach (var activated in activations.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var app in apps)
            {
                try { await EnsureCurrentAsync(activated, app.Name, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One app failing must not keep Settings from starting; the next
                    // activation retries.
                    logger.LogWarning(error, "OS app {App} did not start in brain {Brain}.", app.Name, activated.BrainId);
                }
            }
        }
    }

    private async Task EnsureCurrentAsync(Activated activated, string name, CancellationToken cancellationToken)
    {
        // The brain's (owner, name) pair is the scope identity the kernel derives everywhere
        // from the caller; the import must land in the activated brain's own scope.
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "os",
            AccountId = activated.OwnerAccountId,
            BrainId = activated.Name,
            Kind = CallerKind.Platform,
            StampedBy = TrustedEdge.Platform,
        });
        var os = await publishers.Resolve("os", cancellationToken);
        var shipped = grains.GetGrain<IApp>(IApp.Key(os.ScopeId, name));
        var source = await shipped.Read().WaitAsync(cancellationToken);
        if (source.Manifest.Title.Length == 0) { return; }
        var app = grains.GetGrain<IApp>(IApp.Key(activated.BrainId, name));
        var snapshot = await app.Read().WaitAsync(cancellationToken);
        if (snapshot.Manifest.Title.Length > 0 && snapshot.Version == source.Version) { return; }
        var bundle = await shipped.Export().WaitAsync(cancellationToken);
        if (snapshot.Manifest.Title.Length > 0)
        {
            // An upgrade keeps what the person chose; only the program is replaced.
            var settings = snapshot.Settings;
            var connections = snapshot.Connections;
            await app.Delete().WaitAsync(cancellationToken);
            await app.Import(bundle).WaitAsync(cancellationToken);
            await app.Configure(new(settings, StringComparer.Ordinal), new(connections, StringComparer.Ordinal)).WaitAsync(cancellationToken);
        }
        else
        {
            await app.Import(bundle).WaitAsync(cancellationToken);
        }
    }
}
