using System.Security.Cryptography;
using DigitalBrain;
using Orleans;

namespace DigitalBrain.Flutter.Workspace;

// The shell is the person's, kept in the brain their session is in: Read and Save are open to any
// caller and the neuron admits only the one whose brain and account/principal digest are its key.
// Everything else is module-only.
[Internal]
internal interface IShellState : INeuron
{
    [Public] Task<ShellRead> Read();
    [Public] Task<ShellRead> Save(long expectedRevision, string operationId, string json);
}
