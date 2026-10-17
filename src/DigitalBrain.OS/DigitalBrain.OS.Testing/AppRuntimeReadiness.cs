using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.OS.Testing;

public static class AppRuntimeReadiness
{
    // Initial persistent-script work, not merely process health or queued assignments.
    // Non-subscribing computational programs intentionally cannot satisfy this contract.
    public static async Task WaitAsync(ITestBrain brain, Person person, CancellationToken ct)
    {
        using var caller = person.AsCaller();
        await TestWait.UntilAsync(token => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(person.BrainId)).Inspect(token),
            state => state.IsReady, TimeSpan.FromSeconds(90), ct);
    }
}
