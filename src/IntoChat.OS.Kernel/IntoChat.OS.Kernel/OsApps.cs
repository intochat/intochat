using DigitalBrain.Capabilities;
using DigitalBrain.Signals;
using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel;

// The operating system's default apps: every script in the folder, plus ShippedApps.Roots.
// A distribution adds its own through OsAppsOptions. Apps.
public sealed record OsApp(string Publisher, string Name);

public sealed class OsAppsOptions
{
    public IList<OsApp> Apps { get; } = [];

    // Publishers whose shipped, unedited apps the workspace migrates to the shipped view on connect.
    // The OS trusts only itself; a distribution adds its own publisher.
    public IList<string> TrustedPublishers { get; } = ["os"];
}

public static class OsAppsHosting
{
    private sealed class OsAppsRegistered;

    // The operating system installs its shipped compiled apps plus ShippedApps.Roots; a distribution's configure only adds.
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
        services.TryAddSingleton<IntoChat.OS.Modules.Flutter.IUiRootProvider, OsWorkspaceRoot>();
        services.TryAddSingleton<IntoChat.OS.Modules.Flutter.Chat.IChatAgentSource, AgentChatSource>();
        return services.AddOperatingSystemBoot(configure);
    }

    public static IServiceCollection AddOperatingSystemBoot(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null) { services.Configure(configure); }
        services.AddOptions<OsAppsOptions>();
        services.AddSingleton(new CompiledAppsRegistration(typeof(SettingsApp).Assembly));
        services.TryAddSingleton<OsDefaults>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICreationHandler, OsDefaults>(provider => provider.GetRequiredService<OsDefaults>()));
        return services;
    }
}

// A new brain is born with the OS defaults. Create awaits this handler inside the owner's own
// turn. A present app is the person's, never replaced here. A circle brain gets none (R125): it
// is born with its own room only, the shipped circleroom unit (ShippedApps.CircleUnits). The folder and ShippedApps.Roots load through the feature loader;
// a distribution's extra apps install through the Marketplace.
internal sealed class OsDefaults(
    IIntoChatOSBrains brains, AppInstaller installer, IOptions<OsAppsOptions> options, ILogger<OsDefaults> logger,
    ICapabilityBroker capabilities, FeatureCatalog catalog, FeatureLoader loader) : ICreationHandler
{
    public async Task Created(string brainId, Created created, CancellationToken cancellationToken)
    {
        if (await (await capabilities.Identity(cancellationToken)).KindOf(brainId, cancellationToken) == BrainKind.Circle)
        {
            await DeployCircleUnits(brains.Get<ISynapseStudio>($"{brainId}/ino/synapses"),
                brains.Get<DigitalBrain.Confirmation.IConfirmationInbox>(DigitalBrain.Confirmation.IConfirmationInbox.Key(brainId)), logger, brainId);
            return;
        }
        await ShippedInstall.Missing(catalog, loader, brainId,
            async name => await brains.Get<IApp>($"{brainId}/apps/{name}").Read().WaitAsync(cancellationToken),
            (error, app) => logger.LogWarning(error, "Default app {App} did not install in brain {Brain}.", app, brainId),
            cancellationToken);
        var shipped = await catalog.BootSet();
        foreach (var app in options.Value.Apps)
        {
            if (shipped.Any(item => item.Publisher == app.Publisher && item.Name == app.Name)) { continue; }
            try
            {
                if ((await brains.Get<IApp>($"{brainId}/apps/{app.Name}").Read().WaitAsync(cancellationToken)).Manifest.Title.Length == 0)
                { await InstallDefault(app, cancellationToken); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One default failing must not keep the others from installing.
                logger.LogWarning(error, "Default app {Publisher}/{App} did not install in brain {Brain}.", app.Publisher, app.Name, brainId);
            }
        }
    }

    // A circle's shipped units ask for no slots, so they install without consent. One that fails is logged and the next connect
    // tries again (OsWorkspaceRoot).
    internal static async Task DeployCircleUnits(ISynapseStudio studio, DigitalBrain.Confirmation.IConfirmationInbox inbox, ILogger logger, string brainId)
    {
        foreach (var alias in ShippedApps.CircleUnits)
        {
            try
            {
                var result = await IntoChat.OS.Apps.ShippedUnits.Deploy(studio, alias, plan => inbox.Resolve(plan, true));
                if (!result.Ok || result.Installs.Values.Any(install => install.Status == DigitalBrain.InstallStatus.Pending)) { logger.LogWarning("Circle unit {Unit} did not deploy in {Brain}: {Reason}", alias, brainId, result.Text); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogWarning(error, "Circle unit {Unit} did not deploy in {Brain}.", alias, brainId); }
        }
    }

    // A default the deployment names may need connections or secrets the person adds later; those grant nothing, so the
    // deployment's choice stands for them. A default that reads the person's data is never installed for them.
    private async Task InstallDefault(OsApp app, CancellationToken cancellationToken)
    {
        InstallRequest Request(AppRequirements? requires) => new(new(app.Publisher, app.Name), AppConsent.Of(requires), Guid.Empty);
        var outcome = await installer.Install(Request(null), cancellationToken);
        if (outcome is { Status: InstallStatus.NeedsConsent, Requires: { } requires } && requires.SemanticTypes is not { Length: > 0 })
        { outcome = await installer.Install(Request(requires), cancellationToken); }
        if (outcome.Status != InstallStatus.Ok)
        { throw new InvalidOperationException($"Default app {app.Publisher}/{app.Name} needs the person's consent: {outcome.Message}"); }
    }
}
