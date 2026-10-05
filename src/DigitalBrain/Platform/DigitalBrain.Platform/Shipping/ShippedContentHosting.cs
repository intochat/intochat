using DigitalBrain.Enforcement;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Shipping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;

namespace DigitalBrain.Platform.Shipping;

public static class ShippedContentHosting
{
    public static IServiceCollection AddShippedContentPublishing(this IServiceCollection services)
    {
        services.AddHostedService<ShippedContentStartup>();
        return services;
    }
}

internal sealed class ShippedContentStartup(IEnumerable<IShippedContentPublisher> content, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!lifetime.ApplicationStarted.IsCancellationRequested)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
        }
        foreach (var source in content)
        {
            foreach (var publisher in source.Publishers)
            {
                stoppingToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
                var previous = RequestContext.Get(CallerContextStamper.RequestContextKey);
                try
                {
                    CallerContextStamper.Stamp(new CallerContext
                    {
                        PrincipalId = publisher, AccountId = publisher, BrainId = publisher,
                        Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform,
                    });
                    await source.PublishAsync(publisher, stoppingToken).WaitAsync(stoppingToken);
                }
                finally
                {
                    if (previous is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
                    else { RequestContext.Set(CallerContextStamper.RequestContextKey, previous); }
                }
            }
        }
    }
}
