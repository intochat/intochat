using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Apps;

// Hands verified prebuilt assemblies to a brain's runtime: staged by hash in bounded chunks, then assigned by name, and the sandbox's
// answer read back. Whoever may assign is decided before this is called (owner review for a package, the host's own catalog for a built-in).
internal static class PrebuiltPrograms
{
    // A call is bounded, so an assembly travels in chunks; how long an assignment waits for the sandbox to say whether the program came up.
    private const int StageChunk = 512 * 1024;
    internal static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);

    internal static async Task Assign(IBrainPrograms programs, string key, IReadOnlyDictionary<string, PrebuiltBehavior> behaviors,
        Dictionary<string, string> settings, string digest, CancellationToken ct)
    {
        // A program already running these exact assemblies has them stored: name them again without moving the bytes.
        if (await Running(programs, key, behaviors, ct)
            && await TryAssign(programs, key, behaviors, settings, ct)) { await Settle(programs, key, digest, ct); return; }
        foreach (var behavior in behaviors.Values)
        {
            for (var offset = 0; ; offset += StageChunk)
            {
                var length = Math.Min(StageChunk, behavior.Assembly.Length - offset);
                var final = offset + length >= behavior.Assembly.Length;
                await programs.Stage(behavior.Hash, offset, behavior.Assembly.AsSpan(offset, length).ToArray(), final).WaitAsync(ct);
                if (final) { break; }
            }
        }
        await programs.AssignPrebuilt(key, behaviors.ToDictionary(pair => pair.Key, pair => pair.Value with { Assembly = [] }, StringComparer.Ordinal), settings).WaitAsync(ct);
        await Settle(programs, key, digest, ct);
    }

    private static async Task<bool> Running(IBrainPrograms programs, string key, IReadOnlyDictionary<string, PrebuiltBehavior> behaviors, CancellationToken ct)
    {
        if (await programs.Describe(key).WaitAsync(ct) is not { } view) { return false; }
        var running = view.Behaviors.ToDictionary(behavior => behavior.Id, behavior => behavior.Hash, StringComparer.Ordinal);
        return behaviors.Count > 0 && behaviors.All(pair => running.TryGetValue(BehaviorIds.For(key, pair.Key), out var hash) && hash == pair.Value.Hash);
    }

    private static async Task<bool> TryAssign(IBrainPrograms programs, string key, IReadOnlyDictionary<string, PrebuiltBehavior> behaviors,
        Dictionary<string, string> settings, CancellationToken ct)
    {
        try
        {
            await programs.AssignPrebuilt(key, behaviors.ToDictionary(pair => pair.Key, pair => pair.Value with { Assembly = [] }, StringComparer.Ordinal), settings).WaitAsync(ct);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task Settle(IBrainPrograms programs, string key, string digest, CancellationToken ct)
    {
        // The outcome is the assigner's to see: a sandbox that kept the previous build, or a build that did not start, is a failed install.
        var settled = await programs.Settled(key, SettleTimeout).WaitAsync(ct);
        if (settled?.Behaviors.FirstOrDefault(behavior => behavior.Status is "UpdateFailed" or "Exited") is { } failed)
        { throw new InvalidOperationException($"The package {digest} was not activated: {failed.Id} {failed.Status}. {failed.Error}"); }
    }
}
