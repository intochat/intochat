using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Host.ServiceDefaults;

// An instance behind a load balancer drains before it stops: /ready turns Unhealthy and the host keeps serving,
// so the balancer's probe takes the instance out before anything shuts down. A roll begins the drain from outside
// (POST /drain) and waits until the front no longer routes the instance; a stop the roll did not begin drains
// here, serving for DrainDelay once stopping begins. Either way the host then stops the web server before
// any hosted service: hosted services stop in reverse registration order, so Orleans (registered after the web
// host) would otherwise stop first while Kestrel still accepts, and a straggler would hang instead of being
// refused. StoppingAsync of every hosted lifecycle service runs before any StopAsync, so awaiting here holds the
// whole shutdown. Without the setting the host stops at once.
internal sealed class Drain(IConfiguration configuration, IServiceProvider services) : IHostedLifecycleService, IHealthCheck
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

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Every IHostedLifecycleService.StoppingAsync runs before any StopAsync, so the drain holds the whole shutdown without
    // blocking a thread inside an ApplicationStopping callback.
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue<TimeSpan?>(HealthProbes.DrainDelayKey) is not { } delay || delay <= TimeSpan.Zero) { return; }
        draining = true;
        // The shutdown token ends the wait early; the server still stops.
        if (!drainedByRoll) { try { await Task.Delay(delay, cancellationToken); } catch (OperationCanceledException) { } }
        if (services.GetService<IServer>() is { } server)
        {
            using var bounded = new CancellationTokenSource(HealthProbes.ServerStop);
            // Kestrel's stop is idempotent: the web host's own StopAsync later awaits the same stop.
            await server.StopAsync(bounded.Token);
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
