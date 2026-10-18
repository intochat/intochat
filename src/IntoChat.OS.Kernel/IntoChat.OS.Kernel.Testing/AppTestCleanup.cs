using IntoChat.OS.Apps;
using IntoChat.OS.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Kernel.Testing;

public static class AppTestCleanup
{
    // A lease does not own server-side apps. Retire the workload explicitly before
    // another test reuses the host; disposing Person only closes its HTTP client.
    public static async Task RetireAsync(ITestBrain brain, Person person, string scope, CancellationToken ct)
    {
        using var caller = person.AsCallerIn(scope);
        var keys = new List<string>();
        await foreach (var instance in brain.Kernel.Instances<IApp>(ct))
        {
            if (IApp.NameOf(scope, instance.Key) is not null) { keys.Add(instance.Key); }
        }
        foreach (var key in keys) { await brain.Get<IApp>(key).Delete().WaitAsync(ct); }
        await TestWait.UntilAsync(_ => brain.Get<IBrainRuntime>(BrainRuntimeKeys.For(scope)).Inspect(ct),
            state => state.Session == SessionStatus.None, TimeSpan.FromSeconds(10), ct);
    }
}
