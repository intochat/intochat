using Microsoft.Extensions.Diagnostics.HealthChecks;
using IntoChat.OS.Host.ServiceDefaults;

namespace IntoChat.OS.Kernel.Tests.Unit;

// A load balancer probes /ready: a dependency blip every instance shares must not take them all out at once,
// so /ready runs only the process's own checks and the drain; /health keeps the dependencies.
public sealed class HealthProbeFacts
{
    private static readonly HealthCheckRegistration Self = Registration("self", HealthProbes.LiveTag);
    private static readonly HealthCheckRegistration Drain = Registration(HealthProbes.DrainTag, HealthProbes.DrainTag);
    private static readonly HealthCheckRegistration Postgres = Registration("postgres");

    [Fact]
    public void ReadinessRunsTheProcessAndTheDrainButNoDependency()
    {
        Assert.True(HealthProbes.Ready(Self));
        Assert.True(HealthProbes.Ready(Drain));
        Assert.False(HealthProbes.Ready(Postgres));
    }

    [Fact]
    public void HealthRunsTheDependenciesButNotTheDrain()
    {
        Assert.True(HealthProbes.Health(Self));
        Assert.True(HealthProbes.Health(Postgres));
        Assert.False(HealthProbes.Health(Drain));
    }

    // A roll drains an instance through its own port; the same POST through the front carries X-Forwarded-For.
    [Fact]
    public void OnlyADirectLocalCallerMayDrain()
    {
        Assert.True(HealthProbes.MayDrain(System.Net.IPAddress.Loopback, forwarded: false));
        Assert.True(HealthProbes.MayDrain(System.Net.IPAddress.IPv6Loopback, forwarded: false));
        Assert.False(HealthProbes.MayDrain(System.Net.IPAddress.Loopback, forwarded: true));
        Assert.False(HealthProbes.MayDrain(System.Net.IPAddress.Parse("10.0.0.7"), forwarded: false));
        Assert.False(HealthProbes.MayDrain(null, forwarded: false));
    }

    // The host waits for the whole stop: the drain, the web server's bounded stop, then every grain's deactivation.
    [Fact]
    public void TheShutdownTimeoutCoversTheDrainTheServerStopAndTheDeactivation()
    {
        var drain = TimeSpan.FromSeconds(8);
        var deactivation = TimeSpan.FromSeconds(30);

        Assert.Equal(drain + HealthProbes.ServerStop + deactivation + IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownMargin,
            IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownTimeout(drain, deactivation));
        Assert.Equal(deactivation + IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownMargin,
            IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownTimeout(null, deactivation));
    }

    [Fact]
    public void SingleSiloMembershipTimeoutsAreTunedForProductionAndDevelopment()
    {
        var prod = new Orleans.Configuration.ClusterMembershipOptions();
        IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ConfigureSingleSiloMembership(prod, isDevelopment: false);
        Assert.Equal(TimeSpan.FromSeconds(10), prod.IAmAliveTablePublishTimeout);
        Assert.Equal(2, prod.NumMissedTableIAmAliveLimit);
        Assert.Equal(TimeSpan.FromSeconds(15), prod.TableRefreshTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), prod.MaxJoinAttemptTime);
        Assert.Equal(TimeSpan.FromMinutes(5), prod.DefunctSiloCleanupPeriod);
        Assert.Equal(TimeSpan.FromHours(1), prod.DefunctSiloExpiration);

        var dev = new Orleans.Configuration.ClusterMembershipOptions();
        IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ConfigureSingleSiloMembership(dev, isDevelopment: true);
        Assert.Equal(TimeSpan.FromSeconds(10), dev.IAmAliveTablePublishTimeout);
        Assert.Equal(2, dev.NumMissedTableIAmAliveLimit);
        Assert.Equal(TimeSpan.FromSeconds(15), dev.TableRefreshTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), dev.MaxJoinAttemptTime);
        Assert.Equal(TimeSpan.FromMinutes(1), dev.DefunctSiloCleanupPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), dev.DefunctSiloExpiration);
    }

    private static HealthCheckRegistration Registration(string name, params string[] tags)
        => new(name, _ => new Healthy(), HealthStatus.Unhealthy, tags);

    private sealed class Healthy : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
