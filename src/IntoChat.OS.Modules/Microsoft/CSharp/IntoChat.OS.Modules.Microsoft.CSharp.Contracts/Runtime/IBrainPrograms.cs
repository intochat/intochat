using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

// A whole program of one owner: every synapse of an app (or a standalone file's one program). The brain
// runtime compiles, stores and assigns it in one call, so a write that does not compile never reaches a
// running behavior.
[Alias("microsoft.csharp.programs"), DefaultGrainType("microsoft.csharp.runtime")]
public interface IBrainPrograms : IGrainWithStringKey
{
    // Interleaved: compiling and storing touch none of the runtime's state, so its other calls go on meanwhile;
    // the compiled program is applied in a turn of its own.
    [AlwaysInterleave] Task<ProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings);
    Task Retire(string owner);
    // A pure read: it never waits for a turn in progress.
    [AlwaysInterleave] Task<ProgramView?> Describe(string owner);
}

[GenerateSerializer, Alias("microsoft.csharp.program-view")]
public sealed record ProgramView(
    [property: Id(0)] string Owner,
    [property: Id(1)] string Hash,
    [property: Id(2)] BehaviorView[] Behaviors);

public static class BehaviorIds
{
    // A behavior is one synapse of one owner's program: "{owner}/{path}". A standalone file's one
    // program has the empty path, so its behavior id is its own key.
    public static string For(string owner, string path) => path.Length == 0 ? owner : owner + "/" + path;
}
