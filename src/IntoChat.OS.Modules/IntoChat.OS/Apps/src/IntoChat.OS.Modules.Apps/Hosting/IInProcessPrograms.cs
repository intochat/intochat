using DigitalBrain.Access;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

// The in-process app host's program assignment, kept here since DigitalBrain 0.18 dropped its IPrograms twin.
[NeuronAlias("programs"), DefaultNeuronType("digitalbrain.programs")]
[Internal]
public interface IInProcessPrograms
{
    [DigitalBrain.Interleaves] Task<InProcessProgramView> Assign(string owner, Dictionary<string, string> sources, Dictionary<string, string> settings);
    [DigitalBrain.Interleaves] Task<InProcessProgramView> AssignPrebuilt(string owner, Dictionary<string, PrebuiltBehavior> behaviors, Dictionary<string, string> settings);
    [DigitalBrain.Interleaves] Task Stage(string hash, int offset, byte[] chunk, bool last);
    Task Retire(string owner);
    [DigitalBrain.Interleaves] Task<InProcessProgramView?> Settled(string owner, TimeSpan timeout);
    [DigitalBrain.Interleaves] Task<InProcessProgramView?> Describe(string owner);
}

public sealed record InProcessProgramView(string Owner, string Hash, BehaviorView[] Behaviors);
