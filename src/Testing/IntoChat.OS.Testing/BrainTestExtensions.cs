using IntoChat.OS;
using IntoChat.OS.Client;
using IntoChat.OS.Client.Orleans;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Testing;

public static class BrainTestExtensions
{
    public static async Task<SignalProbe<T>> Observe<T>(this IIntoChatOS brain, INeuron source, CancellationToken cancellationToken = default) where T : Signal
    {
        ArgumentNullException.ThrowIfNull(brain);
        var subscriptions = brain as IBrainSubscriptions ?? throw new ArgumentException("Observe needs a brain that hands out subscription handles (the test harness brains do).", nameof(brain));
        var subscription = await subscriptions.SubscribeAsync<T>(source, cancellationToken).ConfigureAwait(false);
        var tracked = brain as ITrackedBrain;
        var probe = new SignalProbe<T>(subscription, tracked?.BufferCapacity ?? new SubscriptionOptions().BufferCapacity, tracked?.Execution ?? new());
        tracked?.Track(probe);
        return probe;
    }

    public static BehaviorRun RunBehavior(this IIntoChatOS brain, Func<IIntoChatOS, CancellationToken, Task> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        var run = new BehaviorRun(brain, body, cancellationToken);
        (brain as ITrackedBrain)?.Track(run);
        return run;
    }
}
