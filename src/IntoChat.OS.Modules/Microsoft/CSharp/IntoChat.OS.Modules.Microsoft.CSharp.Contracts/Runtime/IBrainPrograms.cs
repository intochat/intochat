
namespace IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

// A whole program of one owner: every synapse of an app (or a standalone file's one program). The brain
// runtime compiles, stores and assigns it in one call, so a write that does not compile never reaches a
// running behavior.
// A plain contract of the brain runtime (06-08); [Internal] as a whole: module code (the Apps runtime, the C# files) assigns
// programs, no edge caller does.
[DigitalBrain.Contracts.NeuronAlias("microsoft.csharp.programs"), DigitalBrain.Contracts.DefaultNeuronType("microsoft.csharp.runtime")]
[DigitalBrain.Access.Internal]
public interface IBrainPrograms
{
    // Interleaved: compiling and storing touch none of the runtime's state, so its other calls go on meanwhile;
    // the compiled program is applied in a turn of its own.
    [DigitalBrain.Interleaves] Task<ProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings);
    Task Retire(string owner);
    // A pure read: it never waits for a turn in progress.
    [DigitalBrain.Interleaves] Task<ProgramView?> Describe(string owner);
}

public sealed record ProgramView(
    string Owner,
    string Hash,
    BehaviorView[] Behaviors);

public static class BehaviorIds
{
    // A behavior is one synapse of one owner's program: "{owner}/{path}". A standalone file's one
    // program has the empty path, so its behavior id is its own key.
    public static string For(string owner, string path) => path.Length == 0 ? owner : owner + "/" + path;
}
