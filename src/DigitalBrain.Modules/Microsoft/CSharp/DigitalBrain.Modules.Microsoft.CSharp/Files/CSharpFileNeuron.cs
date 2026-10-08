using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Assemblies;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using DigitalBrain.Microsoft.CSharp.Runtime;

namespace DigitalBrain.Microsoft.CSharp.Files;

// A behavior is a record: its program, settings and the assembly it compiled to. Start and Swap
// compile and store it, then hand it to the brain's runtime; running it, its subscriptions and its
// failures belong to that runtime and its one sandbox session.
[GrainType("microsoft.csharp.file")]
internal sealed partial class CSharpFileNeuron(
    [PersistentState("state", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<CSharpFileState> store,
    CSharpScriptCheck compiler,
    IBehaviorAssemblies assemblies)
    : Neuron<CSharpFileState>(store), ICSharpFile, ICSharpAppBinding
{
    internal const int MaximumSourceBytes = 128 * 1024;
    private const int MaximumSettings = 64;
    private string FileId => this.GetPrimaryKeyString();

    // The key leads with its brain (CSharpFileKeys), so the runtime is found without a stamped caller.
    private IBrainRuntimeEdge Runtime
    {
        get
        {
            var separator = FileId.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0) { throw new InvalidOperationException($"'{FileId}' is not a behavior key; use CSharpFileKeys.For(brain, name)."); }
            return GrainFactory.GetGrain<IBrainRuntimeEdge>(BrainRuntimeKeys.For(FileId[..separator]));
        }
    }

    internal void RequireBoundAccess(GrainId? source, string method)
    {
        if (Snapshot.AppId is not { } app) { return; }
        if (Snapshot.AppBrain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("This behavior belongs to another brain."); }
        if (method is nameof(Write) or nameof(Configure) or nameof(Start) or nameof(Swap) or nameof(Stop) or nameof(Delete)
            && (source?.Type.ToString() != DigitalBrain.Framework.AppGrainType.Name || source?.Key.ToString() != app))
        { throw new UnauthorizedAccessException("Only the owning Apps grain may change a bound behavior."); }
    }

    public async Task BindApp(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var brain = BrainScope.CurrentId();
        if (Snapshot.AppId is not null && (Snapshot.AppId != appId || Snapshot.AppBrain != brain))
        { throw new UnauthorizedAccessException("The behavior is already bound to another app."); }
        if (Snapshot.AppId is not null) { return; }
        if (Snapshot.Wanted) { await Runtime.Retire(FileId); }
        await Save(Snapshot with { AppId = appId, AppBrain = brain, Wanted = false }, new CSharpFileChanged(FileId));
    }

    public async Task<CSharpFileSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Describe(Snapshot.Wanted ? await Runtime.Describe(FileId) : null);

    public Task Write(string program, CancellationToken cancellationToken = default)
    {
        RequireSize(program);
        return Save(Snapshot with { Program = program }, new CSharpFileChanged(FileId));
    }

    public Task Configure(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Count > MaximumSettings) { throw new ArgumentException($"At most {MaximumSettings} settings are allowed.", nameof(settings)); }
        if (settings.Keys.FirstOrDefault(name => !SettingName().IsMatch(name)) is { } invalid)
        { throw new ArgumentException($"Setting '{invalid}' must contain only letters, digits and underscores.", nameof(settings)); }
        return Save(Snapshot with { Settings = new(settings, StringComparer.Ordinal) }, new CSharpFileChanged(FileId));
    }

    public Task<CSharpFileSnapshot> Start(CancellationToken cancellationToken = default) => AssignAsync(Snapshot.Program, cancellationToken);

    public Task<CSharpFileSnapshot> Swap(string source, CancellationToken cancellationToken = default)
    {
        RequireSize(source);
        return AssignAsync(source, cancellationToken);
    }

    public async Task<CSharpFileSnapshot> Stop(CancellationToken cancellationToken = default)
    {
        await Runtime.Retire(FileId);
        await Save(Snapshot with { Wanted = false }, new CSharpFileChanged(FileId));
        return Describe(null);
    }

    public async Task Delete(CancellationToken cancellationToken = default)
    {
        if (FileId.Contains('/', StringComparison.Ordinal)) { await Runtime.Retire(FileId); }
        await Save(new CSharpFileState(), new CSharpFileChanged(FileId));
        DeactivateOnIdle();
    }

    public Task<string> ReadLogs(int tail = 200, CancellationToken cancellationToken = default)
        => Snapshot.Wanted ? Runtime.Logs(FileId, tail) : Task.FromResult("");

    // Compiles before anything is recorded: a source that does not compile is refused, never assigned.
    private async Task<CSharpFileSnapshot> AssignAsync(string source, CancellationToken cancellationToken)
    {
        if (Snapshot.AppId is not null && Snapshot.AppBrain != BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("This behavior belongs to another brain."); }
        if (string.IsNullOrWhiteSpace(source)) { throw new InvalidOperationException("Write the C# source before starting it."); }
        var compiled = compiler.Compile(new Dictionary<string, string>(StringComparer.Ordinal) { [FileId] = source });
        if (!compiled.Success)
        { throw new InvalidOperationException("The behavior does not compile: " + string.Join("; ", compiled.Errors.Select(error => $"{error.Id} line {error.Line}: {error.Message}"))); }
        var behavior = compiled.Assemblies[FileId];
        await assemblies.Put(behavior, cancellationToken);
        var owner = CallerContextStamper.TryGet(out var caller) ? caller : Snapshot.Owner;
        await Save(Snapshot with { Program = source, Hash = behavior.Hash, Wanted = true, Owner = owner }, new CSharpFileChanged(FileId));
        await Runtime.Assign(FileId, behavior.Hash, Snapshot.Settings, Snapshot.Owner, AppIdentity());
        return Describe(await Runtime.Describe(FileId));
    }

    // A behavior bound to an app acts as that app; an unbound one acts as itself.
    private string AppIdentity()
        => Snapshot.AppId is { } app && Snapshot.Owner is { } owner && Snapshot.AppBrain == BrainScope.Create(owner.AccountId, owner.BrainId).Id ? app : FileId;

    private static void RequireSize(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes) { throw new ArgumentException($"Source exceeds {MaximumSourceBytes} bytes.", nameof(source)); }
    }

    private CSharpFileSnapshot Describe(BehaviorView? view)
        => new(FileId, Snapshot.Program, Snapshot.Settings, Snapshot.Hash, view?.Status ?? CSharpFileStatus.Stopped,
            null, Snapshot.Wanted, view?.Failures ?? 0);

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex SettingName();
}
