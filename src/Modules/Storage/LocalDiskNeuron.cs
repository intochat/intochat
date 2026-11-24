using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Confirmation;
using DigitalBrain.Operations;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.LocalDisk;

internal sealed class LocalDiskState
{
    public string[] RootIds { get; set; } = [];
    public string[] RootPaths { get; set; } = [];
}

[DigitalBrain.Signal("local-disk.roots")]
internal sealed record LocalDiskRootsChanged(string Action) : DigitalBrain.Signal;

// Local disks on this computer. Every call is confined; the filesystem work is LocalDiskStorage.
[DigitalBrain.Neurons.Neuron("local-disk", State = "local-disk")]
internal sealed class LocalDiskNeuron(
    IOperations operations,
    IConfirmations? confirmations = null,
    IStorageWriteAuthorizer? authorizer = null) : DigitalBrain.Neurons.Neuron<LocalDiskState>, ILocalDisk, IStorage
{
    internal const string TypeName = "local-disk";
    internal const string KeyName = "main";

    private LocalDiskStorage Engine => new(Current(), operations, DriveVolumeSource.Instance);

    public Task<string[]> Roots() => Task.FromResult(Paths());

    public async Task AllowRoot(string path)
    {
        var full = LocalDiskRoots.Prepare(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (State.RootPaths.Any(root => string.Equals(root, full, comparison))) { return; }
        await Save(new LocalDiskState
        {
            RootIds = [.. State.RootIds, Guid.NewGuid().ToString("N")],
            RootPaths = [.. State.RootPaths, full],
        }, new LocalDiskRootsChanged("allow")).ConfigureAwait(true);
    }

    public async Task RevokeRoot(string path)
    {
        var full = PathConfinement.Normalize(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var kept = State.RootPaths
            .Select((root, index) => (root, index))
            .Where(pair => !string.Equals(pair.root, full, comparison))
            .ToArray();
        await Save(new LocalDiskState
        {
            RootIds = [.. kept.Select(pair => State.RootIds[pair.index])],
            RootPaths = [.. kept.Select(pair => pair.root)],
        }, new LocalDiskRootsChanged("revoke")).ConfigureAwait(true);
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default)
    {
        foreach (var path in Paths()) { _ = PathConfinement.Resolve(Paths(), path); }
        return Engine.Describe(ct);
    }

    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
    {
        foreach (var path in Paths()) { _ = PathConfinement.Normalize(path); }
        return Engine.Roots(ct);
    }

    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        _ = PathConfinement.Resolve(Paths(), PathOf(folder));
        return Engine.List(folder, query, ct);
    }

    public Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        _ = PathConfinement.Containing(Paths(), PathOf(id));
        return Engine.Stat(id, ct);
    }

    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        _ = PathConfinement.Resolve(Paths(), PathOf(id));
        return Engine.Read(id, range, ct);
    }

    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Source is { } source) { _ = PathConfinement.Resolve(Paths(), PathOf(source)); }
        if (plan.TargetFolder is { } folder) { _ = PathConfinement.Resolve(Paths(), PathOf(folder)); }
        await AuthorizeWrite(plan, ct).ConfigureAwait(true);
        return await Engine.Write(plan, ct).ConfigureAwait(true);
    }

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var path in Paths()) { _ = PathConfinement.Resolve(Paths(), path); }
        if (query.Within is { } within) { _ = PathConfinement.Resolve(Paths(), PathOf(within)); }
        return Engine.Search(query, ct);
    }

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
    {
        _ = PathConfinement.Containing(Paths(), PathOf(id));
        return Engine.Ancestors(id, ct);
    }

    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        _ = PathConfinement.Resolve(Paths(), PathOf(item));
        return Engine.OpenRead(item, range, ct);
    }

    // Stages .{name}.intochat-partial beside the destination, then File.Move(overwrite: false).
    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var folder = plan.TargetFolder ?? throw new Unsupported(null);
        _ = PathConfinement.ResolveForCreate(Paths(), PathOf(folder), plan.Name ?? "file", PathConfinement.Current);
        await AuthorizeWrite(plan, ct).ConfigureAwait(true);
        return await Engine.OpenWrite(plan, ct).ConfigureAwait(true);
    }

    private async Task AuthorizeWrite(WritePlan plan, CancellationToken ct)
    {
        if (authorizer is not null && await authorizer.Consume(Context.Id, plan, ct).ConfigureAwait(true)) { return; }
        if (plan.DryRun) { return; }
        // Fail closed: a host without the confirmation gate must not write on anyone's behalf.
        if (confirmations is null) { throw new InvalidOperationException("Local disk writes need the confirmation gate, and this host has none."); }
        var descriptor = await Describe(ct).ConfigureAwait(true);
        var connection = new CapabilityConnection<IStorage>("storage", descriptor.Label, this);
        var runner = new StorageWriteRunner((_, _) => Task.FromResult(connection),
            (_, _, _) => throw new InvalidOperationException("A confirmation does not execute writes."));
        var question = await runner.Confirmation(plan, ct).ConfigureAwait(true);
        var answer = await confirmations.Confirm(question, ct).ConfigureAwait(true);
        if (answer.Outcome != ConfirmationOutcome.Approved)
        {
            throw new Denied(plan.Source ?? plan.TargetFolder);
        }
    }

    private LocalRoot[] Current()
    {
        var count = Math.Min(State.RootIds.Length, State.RootPaths.Length);
        var roots = new LocalRoot[count];
        for (var index = 0; index < count; index++) { roots[index] = new LocalRoot(State.RootIds[index], PathConfinement.Normalize(State.RootPaths[index])); }
        return roots;
    }

    private string[] Paths() => [.. Current().Select(root => root.Path)];

    private string PathOf(ItemId id)
    {
        var (rootId, relative) = LocalItemId.Read(id);
        var root = Current().FirstOrDefault(item => item.Id == rootId) ?? throw new NotFound(id);
        string combined;
        if (relative.Length == 0)
        {
            combined = root.Path;
        }
        else
        {
            if (!SafeRelativePath.TryParse(relative, out var safeRel))
            {
                throw new Denied(id);
            }
            try
            {
                combined = safeRel.ResolveUnder(root.Path);
            }
            catch (InvalidOperationException error)
            {
                throw new Denied(id, error);
            }
        }
        return PathConfinement.Resolve(Paths(), combined);
    }
}
