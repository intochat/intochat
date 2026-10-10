using DigitalBrain.ServiceDefaults;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DigitalBrain.OS.Tests.Unit;

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

    private static HealthCheckRegistration Registration(string name, params string[] tags)
        => new(name, _ => new Healthy(), HealthStatus.Unhealthy, tags);

    private sealed class Healthy : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }
}
