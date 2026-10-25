using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Kernel.Testing;

public static class AppRuntimeReadiness
{
    // Initial persistent-script work, not merely process health or queued assignments.
    // Non-subscribing computational programs intentionally cannot satisfy this contract.
    public static async Task WaitAsync(ITestBrain brain, Person person, CancellationToken ct)
    {
        using var caller = person.AsCaller();
        var runtime = brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(person.BrainId));
        try { await TestWait.UntilAsync(runtime.Inspect, state => state.IsReady, TimeSpan.FromSeconds(90), ct); }
        catch (TimeoutException error)
        {
            // Which behavior is unready: its status and error, and any subscription with undelivered signals.
            var last = await runtime.Inspect(ct);
            var behaviors = last.Behaviors.Select(behavior => $"{behavior.Id} [{behavior.Status}]{(behavior.Error is { } failed ? " error: " + failed : "")} runs {behavior.Runs}"
                + string.Concat(behavior.Subscriptions.Where(subscription => subscription.Pending > 0)
                    .Select(subscription => $" pending {subscription.Pending} on {subscription.Neuron} {subscription.Signal}"))
                + (behavior.Subscriptions.Length == 0 ? " no subscriptions" : ""));
            throw new TimeoutException($"The brain runtime did not become ready (session {last.Session}, idle {last.IsIdle}):\n" + string.Join("\n", behaviors), error);
        }
    }
}
