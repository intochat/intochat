using DigitalBrain.Apps;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
        services.AddShippedApps(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", publisher: "os");
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
            foreach (var app in new[] { new OsApp("os", "settings"), new OsApp("os", "assistant"), new OsApp("os", "datavisualizer") })
            { if (!os.Apps.Contains(app)) { os.Apps.Add(app); } }
        });
        return services.AddOperatingSystemBoot(configure);
    }

    public static IServiceCollection AddOperatingSystemBoot(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null) { services.Configure(configure); }
        services.TryAddSingleton<OsDefaults>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICreationHandler, OsDefaults>(provider => provider.GetRequiredService<OsDefaults>()));
        services.AddHostedService<OsBootService>();
        services.AddHealthChecks().AddCheck<OsDefaultsPublished>("os-defaults", tags: ["ready"]);
        return services;
    }
}

// A brain, as the OS needs to address it: its id and the account that owns it.
internal sealed record BrainBirth(string BrainId, string AccountId)
{
    // The kernel's id is "kernel/<brain id>"; the brain id is the key.
    public static BrainBirth From(Signal signal, Owner owner)
        => new(GrainId.Parse(signal.Publisher.Name).Key.ToString()!, owner.AccountId);
}

// A new brain is born with the OS defaults: Create awaits this handler, so the first thing a
// person sees in a new brain already has its apps. Every default installs through the same
// Marketplace Install anyone uses; a present app is the person's, never replaced here.
internal sealed class OsDefaults(
    IGrainFactory grains, AppInstaller installer, IOptions<OsAppsOptions> options, ILogger<OsDefaults> logger) : ICreationHandler
{
    public OsApp[] Apps => [.. options.Value.Apps];

    public Task Created(IKernel kernel, Created created, CancellationToken cancellationToken)
        => InstallAsync(new BrainBirth(kernel.GetPrimaryKeyString(), created.Owner.AccountId), Apps, cancellationToken);

    public async Task InstallAsync(BrainBirth activated, OsApp[] apps, CancellationToken cancellationToken)
    {
        // Installing speaks for the brain's owner as the platform; the caller's own context
        // comes back afterwards, since a provisioner runs inside the caller's turn.
        var caller = CallerContextStamper.TryGet(out var current) ? current : null;
        try
        {
            foreach (var app in apps)
            {
                try { await EnsureInstalledAsync(activated, app, cancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One default failing must not keep the others from installing; the next
                    // activation retries.
                    logger.LogWarning(error, "Default app {Publisher}/{App} did not install in brain {Brain}.", app.Publisher, app.Name, activated.BrainId);
                }
            }
        }
        finally
        {
            if (caller is null) { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, caller); }
        }
    }

    private async Task EnsureInstalledAsync(BrainBirth activated, OsApp app, CancellationToken cancellationToken)
    {
        // The brain's (owner, name) pair is the scope identity the kernel derives everywhere
        // from the caller; the install must land in the activated brain's own scope.
        CallerContextStamper.Stamp(new CallerContext
        {
            PrincipalId = "os",
            AccountId = activated.AccountId,
            BrainId = activated.BrainId,
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
        if (listing.Source is AppSource.Brain source && BrainScope.Create(source.AccountId, source.BrainId).Id == activated.BrainId) { return; }
        // A default is offered once: one the brain already has counts as offered, and a person
        // who deletes it keeps it deleted.
        var index = grains.GetGrain<IAppIndex>(activated.BrainId);
        var offer = app.Publisher + "/" + app.Name;
        if (await index.Offered(offer).WaitAsync(cancellationToken)) { return; }
        if ((await grains.GetGrain<IApp>(IApp.Key(activated.BrainId, app.Name)).Read().WaitAsync(cancellationToken)).Manifest.Title.Length == 0)
        { await installer.Install(app.Publisher, app.Name, cancellationToken); }
        await index.Offer(offer).WaitAsync(cancellationToken);
    }
}

// Catch-up for brains that already exist: on every activation, and whenever a default is
// published, every default a brain does not have yet installs. A new brain needs none of this;
// Establish provisions it.
internal sealed class OsBootService(LocalSignalHub signals, OsDefaults defaults) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var apps = defaults.Apps;
        if (apps.Length == 0) { return; }
        var brains = new System.Collections.Concurrent.ConcurrentDictionary<string, BrainBirth>(StringComparer.Ordinal);
        using var activations = signals.Subscribe<Activated>();
        using var published = signals.Subscribe<DigitalBrain.Apps.Signals.AppPublished>();
        await Task.WhenAll(OnActivations(), OnPublished());

        async Task OnActivations()
        {
            await foreach (var activated in activations.Reader.ReadAllAsync(stoppingToken))
            {
                var brain = BrainBirth.From(activated, activated.Owner);
                brains[brain.BrainId] = brain;
                await defaults.InstallAsync(brain, apps, stoppingToken);
            }
        }

        async Task OnPublished()
        {
            await foreach (var app in published.Reader.ReadAllAsync(stoppingToken))
            {
                var matching = apps.Where(item => item.Name == app.Name).ToArray();
                if (matching.Length == 0) { continue; }
                foreach (var activated in brains.Values) { await defaults.InstallAsync(activated, matching, stoppingToken); }
            }
        }
    }
}

// The host is ready when a new brain can be born complete: every default is in the Marketplace.
internal sealed class OsDefaultsPublished(IGrainFactory grains, OsDefaults defaults) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var marketplace = grains.GetGrain<IMarketplace>(IMarketplace.Key);
        foreach (var app in defaults.Apps)
        {
            if (await marketplace.Read(app.Publisher, app.Name).WaitAsync(cancellationToken) is null)
            { return HealthCheckResult.Unhealthy($"{app.Publisher}/{app.Name} is not published yet."); }
        }
        return HealthCheckResult.Healthy();
    }
}
