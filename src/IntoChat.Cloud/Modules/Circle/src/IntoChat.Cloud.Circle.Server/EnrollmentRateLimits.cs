using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace IntoChat.Cloud.Circle.Server;

// T-10-03, T-10-20: fixed windows per remote address (checked before any redeem) and per circle (checked once the
// redeem names the circle). Partitions are in-process per replica.
internal sealed class EnrollmentRateLimits : IDisposable
{
    private readonly PartitionedRateLimiter<string> _byAddress;
    private readonly PartitionedRateLimiter<string> _byCircle;

    public EnrollmentRateLimits(IOptions<CircleServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _byAddress = Fixed(settings.EnrollPerAddress, settings.EnrollPerAddressWindow);
        _byCircle = Fixed(settings.EnrollPerCircle, settings.EnrollPerCircleWindow);
    }

    public bool TryAddress(string address) => Acquire(_byAddress, address);

    public bool TryCircle(string circleId) => Acquire(_byCircle, circleId);

    public void Dispose()
    {
        _byAddress.Dispose();
        _byCircle.Dispose();
    }

    private static bool Acquire(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        return lease.IsAcquired;
    }

    private static PartitionedRateLimiter<string> Fixed(int permits, TimeSpan window)
        => PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
}
