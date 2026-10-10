using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.ServiceDefaults;

// An instance behind a load balancer drains before it stops: /ready turns Unhealthy and the host keeps serving,
// so the balancer's probe takes the instance out before anything shuts down. A roll begins the drain from outside
// (POST /drain) and waits until the front no longer routes the instance; a stop the roll did not begin drains
// here, serving for DrainDelay once stopping begins. Either way the host then stops the web server before
// any hosted service: hosted services stop in reverse registration order, so Orleans (registered after the web
// host) would otherwise stop first while Kestrel still accepts, and a straggler would hang instead of being
// refused. ApplicationStopping fires before any hosted service stops and its callbacks run synchronously, so
// blocking there holds the whole shutdown. Without the setting the host stops at once.
internal sealed class Drain(IConfiguration configuration, IHostApplicationLifetime lifetime, IServiceProvider services) : IHostedService, IHealthCheck
{
    private volatile bool draining;
    // Begun from outside (POST /drain): the roll waited until the front stopped routing this instance before it
    // stopped it, so the stop does not wait again. Seen live: DCP on Windows kills a stopping executable after a
    // few seconds, and a second DrainDelay at stop meant Orleans never left the cluster gracefully.
    private volatile bool drainedByRoll;

    public void Begin()
    {
        drainedByRoll = true;
        draining = true;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(draining ? HealthCheckResult.Unhealthy("draining") : HealthCheckResult.Healthy());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue<TimeSpan?>(HealthProbes.DrainDelayKey) is { } delay && delay > TimeSpan.Zero)
        {
            lifetime.ApplicationStopping.Register(() =>
            {
                draining = true;
                if (!drainedByRoll) { Thread.Sleep(delay); }
                if (services.GetService<IServer>() is { } server)
                {
                    using var bounded = new CancellationTokenSource(HealthProbes.ServerStop);
                    // Kestrel's stop is idempotent: the web host's own StopAsync later awaits the same stop.
                    server.StopAsync(bounded.Token).GetAwaiter().GetResult();
                }
            });
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
