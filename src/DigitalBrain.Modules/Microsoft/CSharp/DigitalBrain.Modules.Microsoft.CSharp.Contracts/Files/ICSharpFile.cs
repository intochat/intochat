using DigitalBrain;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp.Files;

// A C# program as a neuron: its source, its run in the sandbox, and the synapses it formed by
// watching. Starting may build a sandbox on any runtime, hence the long timeouts.
[Alias("microsoft.csharp.file"), DefaultGrainType("microsoft.csharp.file")]
public interface ICSharpFile : INeuron
{
    Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default);
    Task Write(string source, CancellationToken cancellationToken = default);
    Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
    [ResponseTimeout("00:05:00")] Task<CSharpFileSnapshot> Start(CancellationToken cancellationToken = default);
    // An edit, not a fresh intent: the new source restarts the run but keeps the file's durable
    // subscriptions and their buffered signals, so a hot-swapped behavior resumes where it was.
    [ResponseTimeout("00:05:00")] Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default);
    [ResponseTimeout("00:05:00")] Task<CSharpFileSnapshot> Stop(CancellationToken cancellationToken = default);
    [ResponseTimeout("00:05:00")] Task Delete(CancellationToken cancellationToken = default);
    Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default);
}
