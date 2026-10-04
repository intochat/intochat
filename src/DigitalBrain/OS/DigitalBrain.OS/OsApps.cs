using DigitalBrain.Apps;
using DigitalBrain.Contracts.Enforcement;
using DigitalBrain.Kernel;
using DigitalBrain.Kernel.Enforcement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;

namespace DigitalBrain.OS;

// The operating system's first-party apps: package ids ("owner/name") that every brain carries,
// installed at the published revision when the brain activates and upgraded when a new revision
// ships. The list is deployment configuration; a distribution names its own.
// One OS app: the package the brain carries, and optionally the legacy package it replaces.
// Parsing happens where the entry is written, so a typo names its own value.
public sealed record OsApp(PackageId Package, PackageId? Replaces = null)
{
    public static OsApp Parse(string package, string? replaces = null)
        => new(ParseId(package), replaces is null ? null : ParseId(replaces));

    private static PackageId ParseId(string value)
    {
        try { return PackageId.Parse(value); }
        catch (Exception error) { throw new FormatException($"Not a package id: '{value}'.", error); }
    }
}

public sealed class OsAppsOptions
{
    public IList<OsApp> Apps { get; } = [];
}

public static class OsAppsHosting
{
    // The operating system names and ships its own apps; a distribution's configure only adds.
    public static IServiceCollection AddOperatingSystemApps(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddShippedApps(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", publisher: "os");
        services.Configure<OsAppsOptions>(os =>
        {
            os.Apps.Add(new(PackageId.Create("os", "settings"), PackageId.Parse("intochat/settings")));
            os.Apps.Add(new(PackageId.Create("os", "assistant")));
        });
        services.Configure<BuiltInSettingsOptions>(options => options.Package ??= PackageId.Create("os", "settings"));
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
// behavior reacts by ensuring the OS apps are installed and current — through IApp and IPackage,
// the same contracts any app uses. Delivery is at-least-once and Activated repeats on every
// activation, so every step reads before it writes; a package that has not shipped yet is
// simply picked up on the next activation.
internal sealed class OsBootService(
    LocalSignalHub signals, IGrainFactory grains, IOptions<OsAppsOptions> options, ILogger<OsBootService> logger)
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
                try { await EnsureCurrentAsync(activated, app, stoppingToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // One app failing must not keep Settings from starting; the next
                    // activation retries.
                    logger.LogWarning(error, "OS app {Package} did not start in brain {Brain}.", app.Package, activated.BrainId);
                }
            }
        }
    }

    private async Task EnsureCurrentAsync(Activated activated, OsApp osApp, CancellationToken cancellationToken)
    {
        var package = osApp.Package;
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
        var published = (await grains.GetGrain<IPackage>(package.ToString()).Read().WaitAsync(cancellationToken)).Published;
        if (published is null) { return; }
        var app = grains.GetGrain<IApp>(activated.BrainId + "/packages/" + package);
        var snapshot = await app.Read().WaitAsync(cancellationToken);
        if (snapshot.Status != AppStatus.Installed)
        {
            await app.Install(new(Guid.NewGuid(), new(package, published), new Dictionary<string, string>())).WaitAsync(cancellationToken);
        }
        else if (snapshot.Revision is { } installed && installed.Revision != published)
        {
            await app.Upgrade(new(Guid.NewGuid(), new(package, published))).WaitAsync(cancellationToken);
        }

        // With the replacement ensured, adopt the package it replaces: the App grain owns the
        // whole transition (storage ownership transfer + retire) and no-ops when the legacy app
        // is gone, so at-least-once activations retry a failed retire harmlessly.
        if (osApp.Replaces is { } legacy)
        {
            await grains.GetGrain<IAppAdoption>(activated.BrainId + "/packages/" + package).Adopt(legacy).WaitAsync(cancellationToken);
        }
    }
}
