using IntoChat.OS;
using Orleans.Metadata;

namespace IntoChat.OS.Microsoft.CSharp.Files;

// A C# behavior as a neuron: its source and settings. Start and Swap compile it and hand it to the
// brain's runtime, which runs it in the brain's sandbox session; they return once that is recorded.
[Alias("microsoft.csharp.file"), DefaultGrainType("microsoft.csharp.file")]
public interface ICSharpFile : INeuron
{
    Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default);
    // The readable name and purpose of a file that exists (a never-written key is refused). A null or
    // blank name keeps the current one; a null purpose keeps it, a blank one clears it.
    Task<CSharpFileSnapshot> Describe(string? name, string? purpose, CancellationToken cancellationToken = default);
    Task Write(string source, CancellationToken cancellationToken = default);
    Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
    Task<CSharpFileSnapshot> Start(CancellationToken cancellationToken = default);
    // An edit, not a fresh intent: the new assembly replaces the old one in the session while the
    // behavior's durable subscriptions and their buffered signals stay, so it resumes where it was.
    Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default);
    Task<CSharpFileSnapshot> Stop(CancellationToken cancellationToken = default);
    Task Delete(CancellationToken cancellationToken = default);
    Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default);
}
