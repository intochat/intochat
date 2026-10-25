using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using TopologyHosting = DigitalBrain.Runtime.Orleans.TopologyHosting;

namespace IntoChat.OS.Aspire.Server;

public static class ServerHostingExtensions
{
    // An optional JSON file of additional configuration the server loads last (the secrets-file
    // pattern): deployment credentials, test seeds, any setting that must not travel as plain env.
    public const string ConfigurationFileKey = "IntoChatOS:ConfigurationFile";
    public const string ConfigurationFileEnvironmentVariable = "IntoChatOS__ConfigurationFile";

    public static IHostApplicationBuilder AddIntoChatOSServer(this IHostApplicationBuilder builder, Action<IntoChatOSServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Every IntoChat.OS server carries the process defaults; hosts cannot forget them.
        builder.AddServiceDefaults();
        // The stop holds the drain (DrainDelay, then the web server's bounded stop), then Orleans deactivates every
        // grain (GrainCollectionOptions.DeactivationTimeout) before it writes Dead: the host waits for all of it.
        builder.Services.AddOptions<HostOptions>().Configure<IOptions<GrainCollectionOptions>, IConfiguration>(
            static (options, grains, configuration) => options.ShutdownTimeout = ShutdownTimeout(configuration.GetValue<TimeSpan?>(HealthProbes.DrainDelayKey), grains.Value.DeactivationTimeout));

        if (builder.Configuration[ConfigurationFileKey] is { Length: > 0 } configurationFile)
        { builder.Configuration.AddJsonFile(configurationFile, optional: false, reloadOnChange: false); }
        // The optional file may carry legacy keys the first check could not see.
        LegacyConfigurationGuard.Verify(builder.Configuration, builder.Environment);
        OpenPostureLoopback.Validate(builder.Configuration);

        ArgumentNullException.ThrowIfNull(configure);
        var definition = new IntoChatOSServerBuilder();
        configure(definition);
        definition.ApplyServiceActions(builder.Services);
        builder.Services.AddSingleton(definition);
        builder.Services.AddHostedService<AccessPolicyValidation>();
        var (modules, statuses) = definition.SelectModules(builder.Configuration);
        // BrainHosting reads the statuses into ModuleInventory; /health shows the unlicensed ones as Degraded.
        builder.Services.AddSingleton<IReadOnlyList<ModuleStatus>>(statuses);
        foreach (var status in statuses.Where(status => status.State == ModuleState.Unlicensed))
        {
            // Untagged: /health reports it, /ready (live and drain checks only) never does.
            builder.Services.AddHealthChecks().AddCheck("module-" + status.Module, () => HealthCheckResult.Degraded(status.Reason));
        }
        builder.Services.AddHostedService<ModuleStatusLogger>();
        // IntoChatOS:Topology is the one setting that picks clustering, grain stores, streams, reminders and secrets (TOPO-01):
        // the digitalbrain topology is selected and validated before the silo exists, and a partial or unknown setting stops
        // the host naming every issue. The cluster store is the runtime BrainLog over the DigitalBrain modules' assemblies
        // (06-02, R4-09); their manifests pass through UseModuleTopology, which refuses a Need the topology does not bind.
        // os never calls AddDigitalBrainRuntime itself: ConfigureTopologySilo does.
        // A Framework module that brings plain neurons names its DigitalBrain module (Module.Neurons, 06-03); it is hosted when
        // the Framework module is selected.
        IReadOnlyList<Type> neuronModules = [.. definition.NeuronModules.Concat(modules.Select(static module => module.Neurons).OfType<Type>()).Distinct()];
        builder.UseKernelStorage([.. neuronModules.Select(static module => module.Assembly).Distinct()]);
        TopologyHosting.UseModuleTopology(builder, neuronModules, RetiredTopologySettings,
            [.. modules.OfType<ITopologyModule>().SelectMany(module => module.TopologyProfiles)]);
        builder.UseOrleans(silo =>
        {
            // The topology first; it runs the host-bound cluster store (BrainLog) last, then the legacy store and journal of
            // the unmigrated Framework neurons compose over the same logs, then the modules.
            TopologyHosting.ConfigureTopologySilo(silo);
            silo.AddLegacyStorage();
            // os grain state keeps the Orleans binary format it had in memory and on blob storage (persisted-format rule):
            // the topology's file and blob stores use the silo serializer, so it is this one, not the JSON default.
            silo.Services.AddSingleton<global::Orleans.Storage.IGrainStorageSerializer>(static services =>
                new global::Orleans.Storage.OrleansGrainStorageSerializer(services.GetRequiredService<global::Orleans.Serialization.Serializer>()));
            // Development silos restart constantly (rolls, aspire restarts): rows of silos that are gone are swept
            // within minutes, not the default week. Startup connectivity validation stays on; machine silos beat often enough that their own stale rows are skipped (R105).
            if (builder.Environment.IsDevelopment())
            {
                silo.Configure<ClusterMembershipOptions>(membership =>
                {
                    membership.DefunctSiloCleanupPeriod = TimeSpan.FromMinutes(1);
                    membership.DefunctSiloExpiration = TimeSpan.FromMinutes(5);
                });
            }
            silo.Services.AddIntoChatOSClient();
            // Orleans hosting already registers one activity-propagation filter pair. An explicit
            // `silo.AddActivityPropagation()` here registered a second pair and doubled every grain
            // call to 4 spans (measured); it was removed so exactly one registration remains.
            if (definition.Dashboard) { global::Orleans.Dashboard.ServiceCollectionExtensions.AddDashboard(silo); }
            silo.AddIntoChatOS(modules);
            // Identity-backed brain membership, connector types, secret redaction and connection bindings (D-18).
            silo.AddIntoChatOSConnections();
            foreach (var module in modules)
            {
                module.Configure(silo);
                silo.Services.AddSingleton(module);
            }
            // The cluster grain storage may be composed after this delegate; startup validation refuses a
            // server whose storage was never wrapped, so no instance registration is silently lost.
            silo.TryAddKernelRegistration();
        });
        // Storage, clustering and reminders are the topology's; the platform composes over whatever it registered.
        builder.AddIntoChatOSPlatform();
        return builder;
    }

    // The retired settings LegacyConfigurationGuard refuses, handed to the topology validator as well (P2-D-11).
    private static readonly DigitalBrain.Runtime.Orleans.RetiredSetting[] RetiredTopologySettings =
        [.. LegacyConfigurationGuard.RetiredSettings.Select(static row => new DigitalBrain.Runtime.Orleans.RetiredSetting(row.Name, row.Replacement))];

    public static WebApplication MapIntoChatOSServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!((IApplicationBuilder)app).Properties.TryAdd("IntoChat.OS.Mapped", true)) { throw new InvalidOperationException("MapIntoChatOSServer runs once per application."); }
        app.UsePlatformHttp();
        if (app.Services.GetRequiredService<IntoChatOSServerBuilder>().Dashboard) { app.MapOrleansDashboard(); }
        // /health, /ready and /alive (ServiceDefaults.HealthProbes): deployments and the YARP front probe them.
        app.MapDefaultEndpoints();
        app.MapIntoChatOSModules();
        app.MapIntoChatOSPlatform();
        return app;
    }

    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(10);

    public static TimeSpan ShutdownTimeout(TimeSpan? drainDelay, TimeSpan deactivationTimeout)
        => (drainDelay is { } delay && delay > TimeSpan.Zero ? delay + HealthProbes.ServerStop : TimeSpan.Zero) + deactivationTimeout + ShutdownMargin;

    private sealed class ModuleStatusLogger(IReadOnlyList<ModuleStatus> statuses, ILoggerFactory loggers) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            var logger = loggers.CreateLogger("IntoChat.OS.Modules");
            foreach (var status in statuses.Where(status => status.State == ModuleState.Unlicensed))
            { logger.LogWarning("Module {Module} is not running: {Reason}", status.Module, status.Reason); }
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AccessPolicyValidation(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<IntoChat.OS.Framework.Modules.IBrainAccess>();
            if (!services.GetServices<IntoChat.OS.Framework.ICallFilterStage>().Any())
            { throw new InvalidOperationException("Register an authorization policy or AddIntoChatOSPlatform before starting the server."); }
            if (!BrainHosting.HasKernelRegistration(services))
            { throw new InvalidOperationException("The cluster grain storage is not wrapped by the kernel registration: register it before the silo delegate runs."); }
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
