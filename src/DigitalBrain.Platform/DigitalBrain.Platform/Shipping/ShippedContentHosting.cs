using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Shipping;
using DigitalBrain.Platform.Enforcement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Shipping;

public static class ShippedContentHosting
{
    public static IServiceCollection AddShippedContentPublishing(this IServiceCollection services)
    {
        services.AddHostedService<ShippedContentStartup>();
        return services;
    }

    // Shipped content is published as its publisher account, stamped by the Platform itself.
    internal static CallerContext PublisherCaller(PublisherBrain publisher) => new()
    {
        PrincipalId = publisher.Username,
        AccountId = publisher.AccountId,
        BrainId = publisher.BrainId,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    };
}

internal sealed class ShippedContentStartup(
    IEnumerable<IShippedContentPublisher> content, IPublisherDirectory publishers,
    IHostApplicationLifetime lifetime, ILogger<ShippedContentStartup> logger) : BackgroundService
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
            foreach (var account in source.Accounts)
            {
                stoppingToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(account);
                PublisherBrain publisher;
                try { publisher = await publishers.Resolve(account, stoppingToken); }
                catch (InvalidOperationException error)
                {
                    logger.LogError(error, "Shipped content for {Account} stays out: the account does not exist.", account);
                    continue;
                }
                using var _ = CallerContextScope.Scoped(ShippedContentHosting.PublisherCaller(publisher));
                await source.PublishAsync(publisher, stoppingToken).WaitAsync(stoppingToken);
            }
        }
    }
}
