using System.Security.Cryptography;
using DigitalBrain;
using Orleans;

namespace DigitalBrain.Flutter.Workspace;

internal interface IShellState : INeuron
{
    Task<ShellRead> Read();
    Task<ShellRead> Save(long expectedRevision, string operationId, string json);
}
