using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace DigitalBrain.Testing.Module;

public static class ModuleTest
{
    public static ModuleTestBuilder Create() => new();
    internal static async Task<ModuleBrain> StartAsync(ModuleOptions? options = null, CancellationToken cancellationToken = default)
    {
        Orleans.Runtime.RequestContext.Remove(DigitalBrain.Framework.CallerContextStamper.RequestContextKey);
        options ??= new();
        options.Execution.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Execution.StartupTimeout);
        deadline.Token.ThrowIfCancellationRequested();
        var modules = ModuleComposition.Resolve(options.Modules);
        ModuleSettingsValidation.ValidatePublicSettings(modules);
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        // The platform refuses to start without a master key or a declared auth posture; each
        // cluster gets a throwaway key and the Open test posture, shared with the HTTP edge so
        // both sides of the brain protect with the same key. A fact that needs Secured overrides
        // the posture through its PrivateConfiguration.
        var masterKey = options.Execution.PrivateConfiguration.GetValueOrDefault(DigitalBrainNames.MasterKeyConfigurationKey)
            ?? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        // One machine per session: a restarted silo is the same machine, with the same identity and journal.
        var machineDirectory = Path.Combine(Path.GetTempPath(), $"digitalbrain-machine-{Guid.NewGuid():N}");
        builder.ConfigureHost(host =>
        {
            host.Configuration.AddInMemoryCollection(TestLogging.QuietDefaults);
            host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DigitalBrainNames.MasterKeyConfigurationKey] = masterKey,
                [DigitalBrain.Framework.Platform.Integrations.IntegrationSchema.Key("csharp", "RunTokenKey")] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                [Platform.Identity.Configuration.AuthOptions.PostureKey] = nameof(Platform.Identity.Configuration.IdentityPosture.Open),
                [$"{Platform.Parts.PartsOptions.Key}:MachineDirectory"] = machineDirectory,
            });
            foreach (var definition in modules)
            {
                host.Configuration.AddInMemoryCollection(definition.Settings);
            }
            host.Configuration.AddInMemoryCollection(options.Execution.PrivateConfiguration);
        });
        // The HTTP edge needs to know which parameter types are silo services when it builds
        // endpoint delegates, so the composed collection's service types are snapshotted here.
        HashSet<Type> siloServiceTypes = [];
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddDigitalBrain(modules.Select(module => module.Module));
            silo.Services.AddDigitalBrainClient();
            silo.AddDigitalBrainPlatform();
            foreach (var module in modules) { module.Configure(silo); }
            silo.AddMemoryGrainStorage(DigitalBrainNames.ClusterGrainStorage);
            silo.AddMemoryGrainStorage(DigitalBrainNames.PlatformGrainStorage);
            if (options.UseReminders) { silo.UseInMemoryReminderService(); }
            options.ConfigureSilo?.Invoke(silo);
            silo.AddKernelRegistration();
            foreach (var descriptor in silo.Services) { siloServiceTypes.Add(descriptor.ServiceType); }
        });
        builder.ConfigureClient(client => { client.AddDigitalBrain(); options.ConfigureClient?.Invoke(client); });
        InProcessTestCluster? cluster = null;
        var lifetime = new TestSessionLifetime(options.Execution);
        try
        {
            cluster = builder.Build();
            lifetime.Own("cluster", cluster);
            await cluster.DeployAsync(deadline.Token).ConfigureAwait(false);
            var brain = new ModuleBrain(cluster, options.Execution, lifetime);
            // R16/R59: the silo itself provisions its default owner at boot; a machine host also registers its mesh
            // and creates the brain.
            if (options.PartLog) { await ProvisionMachineAsync(brain).WaitAsync(deadline.Token).ConfigureAwait(false); }
            if (options.HttpEdge)
            {
                var edge = await ModuleHttpEdge.StartAsync(brain, modules, options, masterKey, siloServiceTypes, deadline.Token).ConfigureAwait(false);
                lifetime.Own("http-edge", edge);
                brain.AttachHttpEdge(edge);
            }
            return brain;
        }
        catch (Exception error)
        {
            try { await lifetime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["StartupRollbackFailure"] = cleanup; }
            throw;
        }
    }

    // The default brain is the Open posture's: account and brain "owner", as an HTTP caller reaches it.
    public const string DefaultAccount = Platform.Identity.AccountSession.DefaultLogin;

    // Through the trust clients, so a machine of a separate trust cluster provisions the same way.
    private static async Task ProvisionMachineAsync(ModuleBrain brain)
    {
        var nodes = brain.SiloServices.GetRequiredService<Platform.Trust.INodeDirectoryClient>();
        for (var i = 0; i < 8; i++) { await nodes.Register($"node-{i}", "platform").ConfigureAwait(false); }
        var scope = BrainScope.Create(DefaultAccount, DefaultAccount).Id;
        // A machine of a separate trust cluster leaves the brain to the fact: the core part goes to whichever
        // machine creates it, and a second machine must not contend for it.
        if (brain.SiloServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Platform.Trust.TrustOptions>>().Value.Gateway is not null) { return; }
        await brain.Grains.GetGrain<IBrain>(scope).Create(new Owner(DefaultAccount)).ConfigureAwait(false);
    }
}
