using System.Text;
using System.Text.RegularExpressions;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Microsoft.CSharp.Files.Signals;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using Orleans;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Files;

// A file is one standalone program of its brain's runtime: its source compiles to a single behavior whose
// id is the file's own key. Running it, its subscriptions and its restarts belong to the runtime's one
// sandbox session.
[GrainType("microsoft.csharp.file")]
internal sealed partial class CSharpFileNeuron(
    [PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<CSharpFileState> store)
    : Neuron<CSharpFileState>(store), ICSharpFile
{
    internal const int MaximumSourceBytes = 128 * 1024;
    private const int MaximumSettings = 64;
    private string FileId => this.GetPrimaryKeyString();

    // The key leads with its brain (CSharpFileKeys), so the runtime is found without a stamped caller.
    private IBrainPrograms Programs
    {
        get
        {
            var brain = BrainKey.BrainOf(FileId);
            if (brain.Length == 0 || brain.Length == FileId.Length) { throw new InvalidOperationException($"'{FileId}' is not a behavior key; use CSharpFileKeys.For(brain, name)."); }
            return GrainFactory.GetGrain<IBrainPrograms>(BrainRuntimeKeys.For(brain));
        }
    }

    public async Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default)
        => View(Snapshot.Wanted ? await Programs.Describe(FileId) : null);

    public async Task<CSharpFileSnapshot> Describe(string? name, string? purpose, CancellationToken cancellationToken = default)
    {
        if (name is { Length: > 120 } || purpose is { Length: > 4000 })
        { throw new ArgumentException("A name is at most 120 characters and a purpose at most 4000."); }
        // Describing names a file that exists; it never creates one.
        if (!Exists) { throw new KeyNotFoundException($"No C# file '{FileId}' exists; write it first."); }
        await Keep(Snapshot with
        {
            Name = string.IsNullOrWhiteSpace(name) ? Snapshot.Name is { Length: > 0 } current ? current : FileId[(FileId.LastIndexOf('/') + 1)..] : name.Trim(),
            Purpose = purpose?.Trim() ?? Snapshot.Purpose,
        }, new CSharpFileChanged(FileId));
        return await Inspect(cancellationToken);
    }

    public Task Write(string program, CancellationToken cancellationToken = default)
    {
        RequireSize(program);
        return Keep(Snapshot with { Program = program }, new CSharpFileChanged(FileId));
    }

    public Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count > MaximumSettings) { throw new ArgumentException($"At most {MaximumSettings} settings are allowed.", nameof(settings)); }
        if (settings.Keys.FirstOrDefault(name => !SettingName().IsMatch(name)) is { } invalid)
        { throw new ArgumentException($"Setting '{invalid}' must contain only letters, digits and underscores.", nameof(settings)); }
        return Keep(Snapshot with { Settings = settings }, new CSharpFileChanged(FileId));
    }

    public Task<CSharpFileSnapshot> Start(CancellationToken cancellationToken = default) => AssignAsync(Snapshot.Program);

    public Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default)
    {
        RequireSize(source);
        return AssignAsync(source);
    }

    public async Task<CSharpFileSnapshot> Stop(CancellationToken cancellationToken = default)
    {
        await Programs.Retire(FileId);
        await Keep(Snapshot with { Wanted = false }, new CSharpFileChanged(FileId));
        return View(null);
    }

    public async Task Delete(CancellationToken cancellationToken = default)
    {
        if (FileId.Contains('/', StringComparison.Ordinal)) { await Programs.Retire(FileId); }
        await Clear(new CSharpFileChanged(FileId));
        DeactivateOnIdle();
    }

    public Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default)
        => Snapshot.Wanted ? GrainFactory.GetGrain<IBrainRuntimeEdge>(Programs.GetPrimaryKeyString()).Logs(FileId, tail) : Task.FromResult("");

    // The runtime compiles; a source that does not compile throws out of Assign and nothing is recorded.
    private async Task<CSharpFileSnapshot> AssignAsync(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) { throw new InvalidOperationException("Write the C# source before starting it."); }
        var program = await Programs.Assign(FileId, new(StringComparer.Ordinal) { [""] = source }, new(Snapshot.Settings, StringComparer.Ordinal));
        var owner = CallerContextStamper.TryGet(out var caller) ? caller : Snapshot.Owner;
        await Keep(Snapshot with { Program = source, Hash = program.Behaviors[0].Hash, Wanted = true, Owner = owner }, new CSharpFileChanged(FileId));
        return View(program);
    }

    // The first write of any kind is the file's creation.
    private Task Keep(CSharpFileState next, CSharpFileChanged changed)
        => Save(next with { Created = next.Created ?? DateTimeOffset.UtcNow }, changed);

    private static void RequireSize(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes) { throw new ArgumentException($"Source exceeds {MaximumSourceBytes} bytes.", nameof(source)); }
    }

    private CSharpFileSnapshot View(ProgramView? program)
    {
        var behavior = program?.Behaviors.FirstOrDefault();
        return new(FileId, Snapshot.Program, Snapshot.Settings, Snapshot.Hash, behavior?.Status ?? "Stopped", behavior?.Error, Snapshot.Wanted, Snapshot.Name, Snapshot.Purpose, Snapshot.Created);
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SettingName();
}
