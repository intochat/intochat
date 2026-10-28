using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IntoChat.Cloud.Auth.Licensing;

// Degraded when the licence is close to expiry or when the in-module re-verify disagrees with the host gate.
// Never Unhealthy: a licence problem must not take the host down (existing users keep Authority identity).
internal sealed class AuthLicenseHealthCheck(AuthLicenseState state, AuthModuleOptions options, TimeProvider clock) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var (_, expiresAt, status) = state.Read();
        if (status != "valid")
        {
            return Task.FromResult(HealthCheckResult.Degraded("auth licence: " + status));
        }

        if (expiresAt is { } expiry && expiry - clock.GetUtcNow() <= TimeSpan.FromDays(options.ExpiryWarningDays))
        {
            return Task.FromResult(HealthCheckResult.Degraded("auth licence: expiring"));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
