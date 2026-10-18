using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Framework;
using IntoChat.OS.Authority;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Storage;
using IntoChat.OS.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace IntoChat.OS.Aspire.Server;

public static class ServerHostingExtensions
{
    // An optional JSON file of additional configuration the server loads last (the secrets-file
    // pattern): deployment credentials, test seeds, any setting that must not travel as plain env.
    public const string ConfigurationFileKey = "DigitalBrain:ConfigurationFile";
    public const string ConfigurationFileEnvironmentVariable = "DigitalBrain__ConfigurationFile";

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

        ArgumentNullException.ThrowIfNull(configure);
        var definition = new IntoChatOSServerBuilder();
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
            silo.Services.AddIntoChatOSClient();
            // Orleans hosting already registers one activity-propagation filter pair. An explicit
            // `silo.AddActivityPropagation()` here registered a second pair and doubled every grain
            // call to 4 spans (measured); it was removed so exactly one registration remains.
            if (definition.Dashboard) { global::Orleans.Dashboard.ServiceCollectionExtensions.AddDashboard(silo); }
            silo.AddIntoChatOS(modules);
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
        builder.AddIntoChatOSPlatform();
        builder.AddBrainStorage();
        return builder;
    }

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

    private sealed class AccessPolicyValidation(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<IntoChat.OS.Framework.IBrainAccess>();
            if (!services.GetServices<IntoChat.OS.Framework.ICallFilterStage>().Any())
            { throw new InvalidOperationException("Register an authorization policy or AddIntoChatOSPlatform before starting the server."); }
            if (!IntoChat.OS.Framework.BrainHosting.HasKernelRegistration(services))
            { throw new InvalidOperationException("The cluster grain storage is not wrapped by the kernel registration: register it before the silo delegate runs."); }
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
