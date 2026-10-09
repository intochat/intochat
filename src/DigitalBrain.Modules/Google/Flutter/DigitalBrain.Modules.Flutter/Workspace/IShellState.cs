using System.Security.Cryptography;
using DigitalBrain;
using Orleans;

namespace DigitalBrain.Flutter.Workspace;

// The shell is the person's, not a brain's: Read and Save are open to any caller and the neuron
// admits only the one whose account/principal digest is its key. Everything else is module-only.
[Internal]
internal interface IShellState : INeuron
{
    [Public] Task<ShellRead> Read();
    [Public] Task<ShellRead> Save(long expectedRevision, string operationId, string json);
}
