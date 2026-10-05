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

// The operating system's default apps: Marketplace entries every new brain installs. The list
// is deployment configuration; a distribution names its own.
public sealed record OsApp(string Publisher, string Name);

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
            // does not install a default twice.
            foreach (var app in new[] { new OsApp("os", "settings"), new OsApp("os", "assistant") })
            { if (!os.Apps.Contains(app)) { os.Apps.Add(app); } }
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
// behavior installs every default the brain does not have yet, through the same Marketplace
// Install anyone uses. A present app is the person's, never replaced here; newer versions are
// offered as updates. A default not yet published is picked up on a later activation.
internal sealed class OsBootService(
    LocalSignalHub signals, IGrainFactory grains, AppInstaller installer, IOptions<OsAppsOptions> options, ILogger<OsBootService> logger)
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
                try { await EnsureInstalledAsync(activated, app, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One default failing must not keep the others from installing; the next
                    // activation retries.
                    logger.LogWarning(error, "Default app {Publisher}/{App} did not install in brain {Brain}.", app.Publisher, app.Name, activated.BrainId);
                }
            }
        }
    }

    private async Task EnsureInstalledAsync(Activated activated, OsApp app, CancellationToken cancellationToken)
    {
        // The brain's (owner, name) pair is the scope identity the kernel derives everywhere
        // from the caller; the install must land in the activated brain's own scope.
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "os",
            AccountId = activated.OwnerAccountId,
            BrainId = activated.Name,
            Kind = CallerKind.Platform,
            StampedBy = TrustedEdge.Platform,
        });
        var listing = await grains.GetGrain<IMarketplace>(IMarketplace.Key).Read(app.Publisher, app.Name).WaitAsync(cancellationToken);
        if (listing is null)
        {
            logger.LogDebug("Default app {Publisher}/{App} is not in the Marketplace yet.", app.Publisher, app.Name);
            return;
        }
        // The publisher's own brain already owns its apps.
        if (BrainScope.Create(listing.PublisherAccountId, listing.PublisherBrainId).Id == activated.BrainId) { return; }
        if ((await grains.GetGrain<IApp>(IApp.Key(activated.BrainId, app.Name)).Read().WaitAsync(cancellationToken)).Manifest.Title.Length > 0) { return; }
        await installer.Install(app.Publisher, app.Name, cancellationToken);
    }
}
