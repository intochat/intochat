using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.ServiceDefaults;

// An instance behind a load balancer drains before it stops: once stopping begins, /ready turns Unhealthy and
// the host keeps serving for DrainDelay, so the balancer's probe takes the instance out before anything shuts
// down. ApplicationStopping fires before any hosted service (Orleans, Kestrel) stops, and its callbacks run
// synchronously, so blocking there holds the whole shutdown. Without the setting the host stops at once.
internal sealed class Drain(IConfiguration configuration, IHostApplicationLifetime lifetime) : IHostedService, IHealthCheck
{
    private volatile bool draining;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(draining ? HealthCheckResult.Unhealthy("draining") : HealthCheckResult.Healthy());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue<TimeSpan?>(HealthProbes.DrainDelayKey) is { } delay && delay > TimeSpan.Zero)
        {
            lifetime.ApplicationStopping.Register(() =>
            {
                draining = true;
                Thread.Sleep(delay);
            });
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
