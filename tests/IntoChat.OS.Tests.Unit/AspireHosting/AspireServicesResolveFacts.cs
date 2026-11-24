using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class AspireServicesResolveFacts
{
    private sealed class ProbeResource(string name) : Resource(name);

    [Fact]
    public async Task TheAppHostContainerOffersTheThreeTypedServices()
    {
        var os = AppHostChainFacts.Compose("local");
        var probe = new ProbeResource("bridge-probe");
        os.ApplicationBuilder.AddResource(probe);

        await using var app = os.ApplicationBuilder.Build();

        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var commands = app.Services.GetRequiredService<ResourceCommandService>();
        var loggers = app.Services.GetRequiredService<ResourceLoggerService>();

        Assert.NotNull(notifications);
        Assert.NotNull(commands);
        Assert.NotNull(loggers);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<ResourceEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        var watcher = Task.Run(async () =>
        {
            await foreach (var ev in notifications.WatchAsync(cts.Token))
            {
                if (string.Equals(ev.Resource.Name, probe.Name, StringComparison.Ordinal))
                {
                    received.TrySetResult(ev);
                    break;
                }
            }
        }, cts.Token);

        await notifications.PublishUpdateAsync(probe, state => state with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, KnownResourceStateStyles.Success),
        });

        var ev = await received.Task.WaitAsync(cts.Token);
        Assert.Equal(probe.Name, ev.Resource.Name);
        Assert.False(string.IsNullOrWhiteSpace(ev.ResourceId));
        Assert.Equal(KnownResourceStates.Running, ev.Snapshot.State?.Text);

        await watcher;
    }
}
