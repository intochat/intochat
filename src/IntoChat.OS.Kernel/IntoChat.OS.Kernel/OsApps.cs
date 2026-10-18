using IntoChat.OS.Apps;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Kernel;

// The operating system's default apps: features every new brain installs from the Marketplace.
// The list is deployment configuration; a distribution names its own.
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

    // The operating system names its own apps; a distribution's configure only adds. The apps
    // themselves are features the host loads from its features folder (AppsOptions.Features).
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
        services.TryAddSingleton<IntoChat.OS.Flutter.IUiRootProvider, OsWorkspaceRoot>();
        services.TryAddSingleton<IntoChat.OS.Flutter.Chat.IChatAgentSource, AgentChatSource>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFeatureMigration, OsInoMigration>());
        services.Configure<OsAppsOptions>(os =>
        {
            // Idempotent so a host that both composes the OS and wires the apps explicitly
            // does not install a default twice.
            foreach (var app in new[] { new OsApp("os", "settings"), new OsApp("os", "router"), new OsApp("os", "calculator"), new OsApp("os", "filmcraft"), new OsApp("os", "photocraft"), new OsApp("os", "datavisualizer"), new OsApp("os", "selfimprove"), new OsApp("os", "memory"), new OsApp("os", "ino"), new OsApp("os", "brainview"), new OsApp("os", "friends"), new OsApp("os", "circle"), new OsApp("os", "workspace"), new OsApp("os", "usage") })
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
        return services;
    }
}

// A new brain is born with the OS defaults: Create awaits this handler inside the owner's own
// turn, so every default installs through the same Marketplace Install anyone uses, as the
// owner. A present app is the person's, never replaced here. A circle brain gets none (R125): it
// is born with its own room only.
internal sealed class OsDefaults(
    IIntoChatOSBrains brains, AppInstaller installer, IOptions<OsAppsOptions> options, ILogger<OsDefaults> logger,
    IntoChat.OS.Framework.Platform.Identity.IIdentityClient identity) : ICreationHandler
{
    public async Task Created(IBrain kernel, Created created, CancellationToken cancellationToken)
    {
        var brainId = kernel.GetPrimaryKeyString();
        if (await identity.KindOf(brainId) == IntoChat.OS.Framework.Platform.Identity.BrainKind.Circle) { return; }
        foreach (var app in options.Value.Apps)
        {
            try
            {
                if ((await brains.Open(brainId).Neuron<IApp>($"apps/{app.Name}").Read().WaitAsync(cancellationToken)).Manifest.Title.Length == 0)
                { await installer.Install(app.Publisher, app.Name, cancellationToken); }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // One default failing must not keep the others from installing.
                logger.LogWarning(error, "Default app {Publisher}/{App} did not install in brain {Brain}.", app.Publisher, app.Name, brainId);
            }
        }
    }
}
