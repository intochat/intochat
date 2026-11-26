using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Confirmation;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.LocalDisk;

// OS module 'cloud': the brain's own workspace storage (LocalCloudNeuron).
[Module(Id)]
public sealed class CloudModule : Module
{
    public const string Id = "cloud";

    // The neuron type of the brain's workspace; its key is "main" (the connection {brain}/local-cloud/main).
    public const string WorkspaceNeuronType = "local-cloud";

    // The key of an isolated workspace for sandbox and test callers of one connector, beside the brain's own.
    public static string SandboxKey(string connector)
    {
        var name = new string([.. (connector ?? string.Empty).Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return "sandbox-" + (name.Length == 0 ? "default" : name);
    }

    public static ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        var runtime = RuntimeOptions.Resolve(services);
        services.TryAddSingleton(sp => new BrainWorkspaceRoots(
            StateHome.Resolve(sp.GetService<IConfiguration>() ?? configuration, sp.GetService<RuntimeOptions>() ?? runtime).Path,
            Path.GetTempPath()));
        services.TryAddSingleton<ConfinedDiskCoordinator>();
    }
}

// Where a brain's workspace lives on this host: {home}/brains/{brain}/workspace. Legacy names the temp folder the old
// files service fell back to ({temp}/intochat-vfs/{brain}), which the workspace copies from once.
internal sealed class BrainWorkspaceRoots(string home, string temp)
{
    public string For(BrainId brain) => Path.Combine(PathConfinement.Normalize(home), "brains", Segment(brain), "workspace");

    public string Legacy(BrainId brain) => Path.Combine(PathConfinement.Normalize(temp), "intochat-vfs", Segment(brain));

    public string Sandbox(BrainId brain, string key) => Path.Combine(PathConfinement.Normalize(home), "brains", Segment(brain), "sandbox", key);

    private static string Segment(BrainId brain)
    {
        var value = brain.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Contains(':', StringComparison.Ordinal)
            || !IntoChat.OS.Host.Primitives.SafeRelativePath.TryParse(value, out var safe) || safe.Value.Contains('/') || safe.Value != value)
        { throw new ArgumentException("The brain id is not a folder name.", nameof(brain)); }
        return value;
    }
}

internal sealed class LocalCloudState
{
    // The folder the workspace keeps its files in, once chosen; an existing folder is never moved.
    public string? RootDirectory { get; set; }
    public long MaxStorageBytes { get; set; } = LocalCloudNeuron.DefaultMaxStorageBytes;
    // The temp-folder files of the old files service were copied in (copy-if-absent, originals left in place).
    public bool Migrated { get; set; }
}

[DigitalBrain.Signal("local-cloud.workspace")]
internal sealed record BrainWorkspaceChanged(string Action, int Copied) : DigitalBrain.Signal;

// The brain's workspace: storage@1 on the confined disk engine with one root, a size cap counted on disk and the
// dot-file and secret names filtered. Its state is stored; its files are on disk and survive restarts.
[Neuron(CloudModule.WorkspaceNeuronType)]
internal sealed class LocalCloudNeuron(
    IOperations operations,
    BrainWorkspaceRoots? workspaces = null,
    IConfirmations? confirmations = null,
    IStorageWriteAuthorizer? authorizer = null,
    ConfinedDiskCoordinator? coordinator = null,
    ILogger<LocalCloudNeuron>? logger = null) : Neuron<LocalCloudState>, IStorage, IStorageUpload
{
    public const string RootId = "workspace";
    public const long DefaultMaxStorageBytes = 1024L * 1024 * 1024; // 1 GiB per brain

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<LocalCloudNeuron>();
    private StorageUploadSessions? _uploads;

    private ConfinedDiskStorage Engine => new([new LocalRoot(RootId, Root())], operations, DriveVolumeSource.Instance, coordinator,
        ConfinedDiskShape.Workspace(State.MaxStorageBytes, WorkspaceNames.IsHiddenOrSecretSegment));

    private StorageUploadSessions Uploads => _uploads ??= new(() => Engine, () => DigitalBrain.Access.UploadScope.Of(Context.Caller), Context.Time);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (State.Migrated || workspaces is null || !IsWorkspace) { return; }
        var root = State.RootDirectory is { Length: > 0 } kept && Directory.Exists(kept) ? kept : workspaces.For(Id.Brain);
        Directory.CreateDirectory(root);
        var (copied, failed) = WorkspaceMigration.CopyIfAbsent(workspaces.Legacy(Id.Brain), root, _logger);
        // A file that could not be copied is tried again on the next activation; the flag is set only when none is left.
        await Save(new LocalCloudState { RootDirectory = root, MaxStorageBytes = State.MaxStorageBytes, Migrated = failed == 0 },
            new BrainWorkspaceChanged(failed == 0 ? "migrated" : "migration-incomplete", copied)).ConfigureAwait(true);
    }

    protected override async Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (_uploads is not null) { await _uploads.DisposeAsync().ConfigureAwait(true); }
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default) => Engine.Describe(ct);
    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => Engine.Roots(ct);
    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => Engine.List(folder, query, ct);
    public Task<Item> Stat(ItemId id, CancellationToken ct = default) => Engine.Stat(id, ct);
    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => Engine.Read(id, range, ct);
    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => Engine.Search(query, ct);
    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => Engine.Ancestors(id, ct);
    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => Engine.OpenRead(item, range, ct);

    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await AuthorizeWrite(plan, ct).ConfigureAwait(true);
        return await Engine.Write(plan, ct).ConfigureAwait(true);
    }

    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await AuthorizeWrite(plan, ct).ConfigureAwait(true);
        return await Engine.OpenWrite(plan, ct).ConfigureAwait(true);
    }

    async Task<string> IStorageUpload.BeginUpload(WritePlan plan, CancellationToken ct)
    {
        await AuthorizeWrite(plan, ct).ConfigureAwait(true);
        return await Uploads.BeginUpload(plan, ct).ConfigureAwait(true);
    }

    Task IStorageUpload.AppendUpload(string upload, long offset, byte[] bytes, CancellationToken ct) => Uploads.AppendUpload(upload, offset, bytes, ct);
    Task<ItemId> IStorageUpload.CommitUpload(string upload, long length, CancellationToken ct) => Uploads.CommitUpload(upload, length, ct);
    Task IStorageUpload.AbortUpload(string upload, CancellationToken ct) => Uploads.AbortUpload(upload, ct);

    // The workspace is the brain's own: the person acting directly, or a module or the kernel of this brain (host code that
    // applied its own rule, such as a file share checking an editor), writes without a question. Anyone else (an app or an
    // agent calling the neuron, a caller of another brain, no caller) needs an approved runner's permit or the owner's
    // confirmation, and a host without the confirmation gate refuses.
    private async Task AuthorizeWrite(WritePlan plan, CancellationToken ct)
    {
        if (IsSameBrainCaller() && Context.Caller is { } caller && caller.Actor switch
        {
            DigitalBrain.Access.Actor.Person => caller.Direct,
            DigitalBrain.Access.Actor.Module or DigitalBrain.Access.Actor.Kernel => true,
            _ => false,
        }) { return; }
        if (authorizer is not null && await authorizer.Consume(Context.Id, plan, ct).ConfigureAwait(true)) { return; }
        if (plan.DryRun) { return; }
        if (confirmations is null) { throw new Denied(plan.Source ?? plan.TargetFolder); }
        var descriptor = await Describe(ct).ConfigureAwait(true);
        var connection = new CapabilityConnection<IStorage>("storage", descriptor.Label, this);
        var runner = new StorageWriteRunner((_, _) => Task.FromResult(connection),
            (_, _, _) => throw new InvalidOperationException("A confirmation does not execute writes."));
        var question = await runner.Confirmation(plan, ct).ConfigureAwait(true);
        var answer = await confirmations.Confirm(question, ct).ConfigureAwait(true);
        if (answer.Outcome != ConfirmationOutcome.Approved) { throw new Denied(plan.Source ?? plan.TargetFolder); }
    }

    // "main" is the brain's workspace; a sandbox key is an isolated folder of its own and never copies anything in.
    private bool IsWorkspace => Id.Key == "main";

    private string Root()
    {
        var folders = workspaces ?? throw new Unavailable(null, new InvalidOperationException("This host has no workspace folder for brains."));
        var root = State.RootDirectory is { Length: > 0 } stored ? stored
            : IsWorkspace ? folders.For(Id.Brain)
            : Id.Key.StartsWith("sandbox-", StringComparison.Ordinal) && !Id.Key.Contains('/', StringComparison.Ordinal) ? folders.Sandbox(Id.Brain, Id.Key)
            : throw new Unavailable(null, new InvalidOperationException("A workspace key is main or a sandbox key."));
        Directory.CreateDirectory(root);
        return PathConfinement.Normalize(root);
    }
}

// The names the workspace never shows or accepts: dot files and folders, and files that usually hold secrets.
internal static class WorkspaceNames
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".env", ".vs", ".vscode", ".idea", ".ssh", ".aws", ".azure", ".npmrc", ".pypirc", ".DS_Store",
        "secrets", "secrets.json", "appsettings.secrets.json",
    };

    private static readonly string[] BlockedExtensions = [".pem", ".key", ".pfx", ".p12", ".kdbx", ".env"];

    public static bool IsHiddenOrSecretSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) { return false; }
        var trimmed = segment.Trim();
        if (trimmed.StartsWith('.') || Blocked.Contains(trimmed)) { return true; }
        return BlockedExtensions.Any(extension => trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }
}

// The one-time copy of the old files service's temp folder into the workspace. Copy-if-absent: a file already in the
// workspace is never replaced. Each file is staged under a partial name and moved in, so an interrupted copy leaves either
// nothing or the whole file; a rerun copies what is still missing. The temp folder is read only and left as it is.
// {legacy}/work goes to the workspace root; {legacy}/local and {legacy}/drive (copies the old service kept of files
// meant for the local disk or a drive) go to "Recovered/local" and "Recovered/drive", so nothing is lost or mixed in.
internal static class WorkspaceMigration
{
    public static (int Copied, int Failed) CopyIfAbsent(string legacy, string root, ILogger logger)
    {
        var copied = 0;
        var failed = 0;
        foreach (var (from, to) in new[] { ("work", ""), ("local", Path.Combine("Recovered", "local")), ("drive", Path.Combine("Recovered", "drive")) })
        {
            var source = Path.Combine(legacy, from);
            if (!Directory.Exists(source) || IsLink(source)) { continue; }
            CopyTree(source, Path.Combine(root, to), logger, ref copied, ref failed);
        }
        return (copied, failed);
    }

    private static void CopyTree(string source, string destination, ILogger logger, ref int copied, ref int failed)
    {
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(source).EnumerateFileSystemInfos().ToList(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "Workspace migration could not read {Folder}; it is left as it is.", source);
            failed++;
            return;
        }
        foreach (var entry in entries)
        {
            if (IsLink(entry.FullName)) { continue; }
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo) { CopyTree(entry.FullName, target, logger, ref copied, ref failed); continue; }
            if (entry.Name.EndsWith(ConfinedDiskStorage.PartialSuffix, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (File.Exists(target) || Directory.Exists(target)) { continue; }
            var stage = Path.Combine(destination, "." + entry.Name + ".migrating" + ConfinedDiskStorage.PartialSuffix);
            try
            {
                Directory.CreateDirectory(destination);
                File.Copy(entry.FullName, stage, overwrite: true);
                File.Move(stage, target, overwrite: false);
                copied++;
                logger.LogInformation("Workspace migration copied {Source} to {Target}.", entry.FullName, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(error, "Workspace migration did not copy {Source}; the original is left in place.", entry.FullName);
                failed++;
                try { if (File.Exists(stage)) { File.Delete(stage); } }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { logger.LogSwallowed(cleanup, "Workspace migration staging cleanup could not finish."); }
            }
        }
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
}
