using DigitalBrain;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.CSharp;

// The C# dialect of a synapse: Read/Write/Start/Stop/Delete are the word's; the rest is how this
// runner hosts it. Inspect is the rich view beside ISynapse.Read, not a hiding of it, because the
// script edge resolves a method by name and argument count across a contract and its bases.
[Alias("microsoft.csharp.file"), DefaultGrainType("microsoft.csharp.file")]
public interface ICSharpFile : ISynapse
{
    Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default);
    Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
    // An edit, not a fresh intent: the new source restarts the run but keeps the file's durable
    // subscriptions and their buffered signals, so a hot-swapped behavior resumes where it was.
    [ResponseTimeout("00:05:00")] Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default);
    // Waits for the trigger's signal instead of running now; every matching signal starts one run that reads it with brain.Trigger<T>().
    Task<CSharpFileSnapshot> Arm(CSharpTrigger trigger, CancellationToken cancellationToken = default);
    Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default);
}
