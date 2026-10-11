using DigitalBrain.Client.Orleans;
using DigitalBrain.Framework;
using DigitalBrain.Platform;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Parts;
using DigitalBrain.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace DigitalBrain.Aspire.Server;

public static class ServerHostingExtensions
{
    // An optional JSON file of additional configuration the server loads last (the secrets-file
    // pattern): deployment credentials, test seeds, any setting that must not travel as plain env.
    public const string ConfigurationFileKey = "DigitalBrain:ConfigurationFile";
    public const string ConfigurationFileEnvironmentVariable = "DigitalBrain__ConfigurationFile";

    public static IHostApplicationBuilder AddDigitalBrainServer(this IHostApplicationBuilder builder, Action<DigitalBrainServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // Every DigitalBrain server carries the process defaults; hosts cannot forget them.
        builder.AddServiceDefaults();
        // The stop holds the drain (DrainDelay, then the web server's bounded stop), then Orleans deactivates every
        // grain (GrainCollectionOptions.DeactivationTimeout) before it writes Dead: the host waits for all of it.
        builder.Services.AddOptions<HostOptions>().Configure<IOptions<GrainCollectionOptions>, IConfiguration>(
            static (options, grains, configuration) => options.ShutdownTimeout = ShutdownTimeout(configuration.GetValue<TimeSpan?>(HealthProbes.DrainDelayKey), grains.Value.DeactivationTimeout));

        if (builder.Configuration[ConfigurationFileKey] is { Length: > 0 } configurationFile)
        { builder.Configuration.AddJsonFile(configurationFile, optional: false, reloadOnChange: false); }

        ArgumentNullException.ThrowIfNull(configure);
        var definition = new DigitalBrainServerBuilder();
        configure(definition);
        definition.ApplyServiceActions(builder.Services);
        builder.Services.AddSingleton(definition);
        builder.Services.AddHostedService<AccessPolicyValidation>();
        var modules = definition.SelectModules(builder.Configuration);
        builder.UseOrleans(silo =>
        {
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
            silo.Services.AddDigitalBrainClient();
            // Orleans hosting already registers one activity-propagation filter pair. An explicit
            // `silo.AddActivityPropagation()` here registered a second pair and doubled every grain
            // call to 4 spans (measured); it was removed so exactly one registration remains.
            if (definition.Dashboard) { global::Orleans.Dashboard.ServiceCollectionExtensions.AddDashboard(silo); }
            silo.AddDigitalBrain(modules);
            foreach (var module in modules)
            {
                module.Configure(silo);
                silo.Services.AddSingleton(module);
            }
            // The cluster grain storage may be composed after this delegate; startup validation refuses a
            // server whose storage was never wrapped, so no instance registration is silently lost.
            silo.TryAddKernelRegistration();
        });
        // Storage, clustering and reminders are the orleans module's; the platform and the machine part
        // log compose over whatever it registered.
        builder.AddDigitalBrainPlatform();
        builder.AddMachinePartLogWhenConfigured();
        return builder;
    }

    public static WebApplication MapDigitalBrainServer(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!((IApplicationBuilder)app).Properties.TryAdd("DigitalBrain.Mapped", true)) { throw new InvalidOperationException("MapDigitalBrainServer runs once per application."); }
        app.UsePlatformHttp();
        if (app.Services.GetRequiredService<DigitalBrainServerBuilder>().Dashboard) { app.MapOrleansDashboard(); }
        // /health, /ready and /alive (ServiceDefaults.HealthProbes): deployments and the YARP front probe them.
        app.MapDefaultEndpoints();
        app.MapDigitalBrainModules();
        app.MapDigitalBrainPlatform();
        return app;
    }

    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(10);

    public static TimeSpan ShutdownTimeout(TimeSpan? drainDelay, TimeSpan deactivationTimeout)
        => (drainDelay is { } delay && delay > TimeSpan.Zero ? delay + HealthProbes.ServerStop : TimeSpan.Zero) + deactivationTimeout + ShutdownMargin;

    private sealed class AccessPolicyValidation(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<DigitalBrain.Framework.IBrainAccess>();
            if (!services.GetServices<DigitalBrain.Framework.ICallFilterStage>().Any())
            { throw new InvalidOperationException("Register an authorization policy or AddDigitalBrainPlatform before starting the server."); }
            if (!DigitalBrain.Framework.BrainHosting.HasKernelRegistration(services))
            { throw new InvalidOperationException("The cluster grain storage is not wrapped by the kernel registration: register it before the silo delegate runs."); }
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
