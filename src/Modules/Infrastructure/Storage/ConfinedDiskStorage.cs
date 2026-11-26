using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Log;
using DigitalBrain.Operations;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.LocalDisk;

internal sealed record LocalRoot(string Id, string Path)
{
    internal static LocalRoot FromPath(string path) => new(path, path);
}

// What one confined-disk storage differs in: label/kind, item id wire codec (ld: vs w:), trash folder name,
// optional disk-usage cap and hidden-name filter (brain workspace), ETag emission, and search traversal bounds.
internal sealed record ConfinedDiskShape(
    string Label,
    string Kind,
    IItemIdCodec? Codec = null,
    string TrashFolderName = ConfinedDiskStorage.DefaultTrashName,
    long? MaxStorageBytes = null,
    Func<string, bool>? HiddenName = null,
    bool IncludeETag = false,
    int? SearchDepth = 6,
    int? SearchCap = 500)
{
    internal static ConfinedDiskShape ThisComputer { get; } = new("This computer", "local-disk");

    internal static ConfinedDiskShape Windows { get; } = new(
        Label: "This PC",
        Kind: "windows",
        Codec: WindowsPathIdCodec.Instance,
        TrashFolderName: ".intochat-trash",
        IncludeETag: true,
        SearchDepth: null,
        SearchCap: null);

    internal static ConfinedDiskShape Workspace(long? maxStorageBytes, Func<string, bool>? hiddenName)
        => new("Workspace", CloudModule.WorkspaceNeuronType, MaxStorageBytes: maxStorageBytes, HiddenName: hiddenName);
}

// Canonical confined-disk storage@1 engine shared by local-disk, local-cloud (brain workspace), and windows PC storage.
// Access recording belongs to the runtime broker, not this type.
internal sealed class ConfinedDiskStorage : IStorage
{
    internal const string DefaultTrashName = ".localdisk-trash";
    internal const string PartialSuffix = ".intochat-partial";
    internal const long MaxWriteBytes = 64L * 1024 * 1024;
    private const ItemFlags Offered = ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder
        | ItemFlags.Copy | ItemFlags.Move | ItemFlags.Rename | ItemFlags.Trash | ItemFlags.Delete;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Func<IReadOnlyList<LocalRoot>> _roots;
    private readonly IOperations? _operations;
    private readonly IVolumeSource _volumes;
    private readonly ConfinedDiskCoordinator? _coordinator;
    private readonly ConfinedDiskShape _shape;
    private readonly IItemIdCodec _codec;

    // Neurons share the host's coordinator, per root folder. An engine made without one (a test of the engine alone)
    // serializes its own commits.
    private readonly SemaphoreSlim _ownGate = new(1, 1);

    internal ConfinedDiskStorage(
        Func<IReadOnlyList<LocalRoot>> currentRoots,
        IOperations? operations = null,
        IVolumeSource? volumes = null,
        ConfinedDiskCoordinator? coordinator = null,
        ConfinedDiskShape? shape = null)
    {
        _roots = currentRoots ?? throw new ArgumentNullException(nameof(currentRoots));
        _operations = operations;
        _volumes = volumes ?? DriveVolumeSource.Instance;
        _coordinator = coordinator;
        _shape = shape ?? ConfinedDiskShape.ThisComputer;
        _codec = _shape.Codec ?? LocalDiskIdCodec.Instance;
    }

    internal ConfinedDiskStorage(
        IReadOnlyList<LocalRoot> roots,
        IOperations? operations = null,
        IVolumeSource? volumes = null,
        ConfinedDiskCoordinator? coordinator = null,
        ConfinedDiskShape? shape = null)
        : this(() => roots, operations, volumes, coordinator, shape)
    {
        ArgumentNullException.ThrowIfNull(roots);
    }

    private IReadOnlyList<LocalRoot> CurrentRoots() => _roots();

    public Task<StorageDescriptor> Describe(CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        var quota = _shape.MaxStorageBytes is { } max
            ? Quota.Known(Math.Min(UsedBytes(roots), max), max)
            : LocalVolumes.QuotaFor([.. roots.Select(root => root.Path)], _volumes);
        return Task.FromResult(new StorageDescriptor(_shape.Label, _shape.Kind, quota, Offered));
    }

    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        return Task.FromResult<IReadOnlyList<Item>>([.. roots.Where(root => Directory.Exists(root.Path)).Select(root => ItemOf(root, ""))]);
    }

    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = CurrentRoots();
        var (root, path) = Full(roots, folder);
        return Task.FromResult(Guard(folder, () =>
        {
            if (!Directory.Exists(path)) { throw File.Exists(path) ? new Unsupported(folder) : new NotFound(folder); }
            ct.ThrowIfCancellationRequested();
            var items = new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(entry => Visible(entry.Name) && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(entry => ItemOf(roots, root, entry));
            return Cut(Sorted(items, query.Sort, query.Descending), query.Cursor, query.PageSize);
        }));
    }

    public Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        var (root, path) = Full(roots, id);
        return Task.FromResult(Guard(id, () =>
            Directory.Exists(path) ? ItemOf(roots, root, new DirectoryInfo(path))
            : File.Exists(path) ? ItemOf(roots, root, new FileInfo(path))
            : throw new NotFound(id)));
    }

    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        var (_, path) = Full(roots, id);
        await using var stream = await OpenRead(id, range, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return new Content(buffer.ToArray(), stream.ContentType, new FileInfo(path).Length);
    }

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var roots = CurrentRoots();
        var starts = query.Within is { } within
            ? [Full(roots, within)]
            : roots.Where(root => Directory.Exists(root.Path)).Select(root => (Root: root, FullPath: root.Path)).ToArray();
        return Task.FromResult(Guard(query.Within, () =>
        {
            var found = new List<Item>();
            var visited = new HashSet<string>(PathComparer);
            foreach (var (root, startPath) in starts)
            {
                Walk(roots, root, startPath, Relative(root.Path, startPath), 0, query.Text, visited, found, ct);
                if (_shape.SearchCap is { } cap && found.Count >= cap) { break; }
            }
            return Cut(Sorted(found, DigitalBrain.Capabilities.Storage.SortKey.FolderThenName, descending: false), query.Cursor, query.PageSize);
        }));
    }

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        var (root, path) = Full(roots, id);
        return Task.FromResult(Guard(id, () =>
        {
            if (!Directory.Exists(path) && !File.Exists(path)) { throw new NotFound(id); }
            var relative = Relative(root.Path, path);
            if (relative.Length == 0) { return []; }
            var chain = new List<Item> { ItemOf(root, "") };
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var walked = "";
            for (var index = 0; index < parts.Length - 1; index++)
            {
                walked = walked.Length == 0 ? parts[index] : walked + "/" + parts[index];
                chain.Add(ItemOf(root, walked));
            }
            return (IReadOnlyList<Item>)chain;
        }));
    }

    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var roots = CurrentRoots();
        var (_, path) = Full(roots, item);
        return Task.FromResult(Guard(item, () =>
        {
            if (Directory.Exists(path)) { throw new Unsupported(item); }
            var file = new FileInfo(path);
            if (!file.Exists) { throw new NotFound(item); }
            Recheck(roots, path, item);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var offset = Math.Min(range?.Offset ?? 0, file.Length);
            var length = range?.Length is { } wanted ? Math.Min(wanted, file.Length - offset) : file.Length - offset;
            stream.Seek(offset, SeekOrigin.Begin);
            Stream content = range is null ? stream : new BoundedStream(stream, length);
            var etag = _shape.IncludeETag ? ETag(file) : null;
            return new StorageReadStream(content, length, ContentType(path), etag, canSeek: range is null && stream.CanSeek, file.LastWriteTimeUtc);
        }));
    }

    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        var host = _operations ?? throw new InvalidOperationException("Confined disk writes need the host's operations.");
        var operation = await host.Start(new ConfinedDiskOperation(plan with { Inline = null }, plan.Inline?.Length,
            plan.Inline is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) : null), ct).ConfigureAwait(false);
        _ = Task.Run(() => CompleteWrite(plan, operation, ct), CancellationToken.None);
        return operation.Id;
    }

    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        if (plan.Kind != WriteKind.WriteFile || plan.DryRun) { throw new Unsupported(plan.TargetFolder); }
        var roots = CurrentRoots();
        return Task.FromResult(Guard(plan.TargetFolder, () =>
        {
            var destination = Prepare(roots, plan).Destination ?? throw new Unsupported(plan.TargetFolder);
            destination = Place(destination, plan.Conflict, source: null, plan.TargetFolder);
            var stage = Partial(destination);
            Recheck(roots, destination, plan.TargetFolder);
            PathConfinement.RequireNoLinks(Path.GetDirectoryName(destination)!);
            var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
            return (StorageWriteStream)new WriteHandle(this, plan, stage, new BoundedWriteStream(stream, MaxWriteBytes, plan.TargetFolder));
        }));
    }

    private async Task CompleteWrite(WritePlan plan, IOperation operation, CancellationToken requested)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requested, operation.Cancellation);
        var ct = cancellation.Token;
        OperationResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            _ = Prepare(CurrentRoots(), plan);
            if (plan.DryRun) { result = new OperationResult.Planned([plan.Kind.ToString()]); }
            else
            {
                await operation.Report(0).ConfigureAwait(false);
                await Apply(plan, ct).ConfigureAwait(false);
                await operation.Report(1).ConfigureAwait(false);
                result = new OperationResult.Succeeded();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = new OperationResult.Cancelled(); }
        catch (StorageException error) { result = new OperationResult.Failed(error.Code, error.Message); }
        catch (UnauthorizedAccessException) { result = new OperationResult.Failed("denied", "Storage access was denied."); }
        catch (IOException error) { result = new OperationResult.Failed(IoCode(error), "The storage change could not be completed."); }
        catch (ArgumentException) { result = new OperationResult.Failed("invalid-plan", "The storage plan is invalid."); }
        await operation.Complete(result).ConfigureAwait(false);
    }

    private (string? Source, string? Destination) Prepare(IReadOnlyList<LocalRoot> roots, WritePlan plan)
    {
        if (!Enum.IsDefined(plan.Kind) || !Enum.IsDefined(plan.Conflict)) { throw new Unsupported(plan.Source); }
        string? source = plan.Source is { } item ? Full(roots, item).FullPath : null;
        if (source is not null)
        {
            if (!Exists(source)) { throw new NotFound(plan.Source); }
            if (IsRoot(roots, source)) { throw new Denied(plan.Source); }
            PathConfinement.RequireNoLinks(source);
        }
        string? destination = plan.Kind switch
        {
            WriteKind.CreateFolder or WriteKind.WriteFile => Path.Combine(Folder(roots, plan.TargetFolder), Safe(plan.Name)),
            WriteKind.Copy or WriteKind.Move => source is null ? throw new Unsupported(null) : Path.Combine(Folder(roots, plan.TargetFolder), Path.GetFileName(source)),
            WriteKind.Rename => source is null ? throw new Unsupported(null) : Path.Combine(Path.GetDirectoryName(source)!, Safe(plan.Name)),
            WriteKind.Delete => source is null ? throw new Unsupported(null) : null,
            _ => throw new Unsupported(plan.Source),
        };
        if (destination is not null)
        {
            Recheck(roots, destination, plan.TargetFolder ?? plan.Source);
            if (source is not null && plan.Kind is WriteKind.Move or WriteKind.Copy && Under(source, destination)) { throw new Denied(plan.Source); }
        }
        return (source, destination);
    }

    private async Task Apply(WritePlan plan, CancellationToken ct)
    {
        if (plan.Kind == WriteKind.WriteFile)
        {
            if (plan.Inline is not { } content) { throw new Unsupported(plan.TargetFolder); }
            if (content.Length > MaxWriteBytes) { throw new QuotaExceeded(plan.TargetFolder); }
            RequireRoom(CurrentRoots(), content.Length, replaced: 0, plan.TargetFolder);
            await using var writer = await OpenWrite(plan, ct).ConfigureAwait(false);
            await writer.Content.WriteAsync(content, ct).ConfigureAwait(false);
            _ = await writer.Commit(ct).ConfigureAwait(false);
            return;
        }
        using var held = await Hold(CurrentRoots(), [plan.Source, plan.TargetFolder], ct).ConfigureAwait(false);
        var roots = CurrentRoots();
        var (source, destination) = Prepare(roots, plan);
        ct.ThrowIfCancellationRequested();
        if (plan.Kind == WriteKind.Delete)
        {
            if (plan.Permanent) { DeletePath(source!); }
            else { Trash(roots, source!); }
            return;
        }
        if (source is not null && plan.Kind is WriteKind.Move or WriteKind.Rename && string.Equals(source, destination, PathComparison))
        {
            if (string.Equals(source, destination, StringComparison.Ordinal)) { return; }
            var intermediate = Partial(source);
            MovePath(source, intermediate, overwrite: false);
            try { MovePath(intermediate, destination!, overwrite: false); }
            catch { MovePath(intermediate, source, overwrite: false); throw; }
            return;
        }
        destination = Place(destination!, plan.Conflict, plan.Kind is WriteKind.Move or WriteKind.Rename ? source : null, plan.Source ?? plan.TargetFolder);
        if (plan.Kind == WriteKind.CreateFolder)
        {
            Directory.CreateDirectory(destination);
            return;
        }
        if (plan.Kind == WriteKind.Copy)
        {
            RequireRoom(roots, SizeOf(source!), replaced: 0, plan.TargetFolder);
            CopyPath(source!, destination, plan.Conflict == ConflictPolicy.Overwrite);
            return;
        }
        MovePath(source!, destination, plan.Conflict == ConflictPolicy.Overwrite);
    }

    private async Task<ItemId> Commit(string stage, WritePlan plan, CancellationToken ct)
    {
        using var held = await Hold(CurrentRoots(), [plan.TargetFolder], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var roots = CurrentRoots();
        var baseDestination = Prepare(roots, plan).Destination ?? throw new Unsupported(plan.TargetFolder);
        var destination = Place(baseDestination, plan.Conflict, source: null, plan.TargetFolder);
        Recheck(roots, destination, plan.TargetFolder);
        var overwrite = plan.Conflict == ConflictPolicy.Overwrite;
        if (!overwrite && Exists(destination)) { throw new Conflict(plan.TargetFolder); }
        if (Directory.Exists(destination)) { throw new Conflict(plan.TargetFolder); }
        RequireRoom(roots, new FileInfo(stage).Length, File.Exists(destination) ? new FileInfo(destination).Length : 0, plan.TargetFolder);
        Guard(plan.TargetFolder, () => { File.Move(stage, destination, overwrite); return true; });
        var root = RootContaining(roots, destination);
        return _codec.Encode(root, destination);
    }

    // Takes the gates of the roots the items are in, in one order, so two plans over two roots cannot wait on each other.
    private async Task<Held> Hold(IReadOnlyList<LocalRoot> roots, ItemId?[] items, CancellationToken ct)
    {
        var folders = items.OfType<ItemId>()
            .Select(item => _codec.Resolve(item, roots).Root.Path)
            .Distinct(PathComparer)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var taken = new List<SemaphoreSlim>(folders.Length);
        try
        {
            foreach (var folder in folders)
            {
                var gate = _coordinator?.GateFor(folder) ?? _ownGate;
                await gate.WaitAsync(ct).ConfigureAwait(false);
                taken.Add(gate);
            }
        }
        catch
        {
            foreach (var gate in taken) { gate.Release(); }
            throw;
        }
        return new Held(taken);
    }

    private sealed class Held(List<SemaphoreSlim> gates) : IDisposable
    {
        public void Dispose()
        {
            foreach (var gate in gates) { gate.Release(); }
        }
    }

    private (LocalRoot Root, string FullPath) Full(IReadOnlyList<LocalRoot> roots, ItemId id)
    {
        var (root, full) = _codec.Resolve(id, roots);
        if (IsPrivateOrHidden(root.Path, full, out var isPrivate))
        {
            throw isPrivate ? new Denied(id) : new NotFound(id);
        }
        return (root, full);
    }

    private static void Recheck(IReadOnlyList<LocalRoot> roots, string path, ItemId? id)
    {
        try
        {
            var full = PathConfinement.Resolve([.. roots.Select(root => root.Path)], path);
            if (roots.All(root => !Under(root.Path, full))) { throw new Denied(id); }
        }
        catch (ConfinementDenied error) { throw new Denied(id, error); }
    }

    private string Folder(IReadOnlyList<LocalRoot> roots, ItemId? id)
    {
        if (id is not { } folder) { throw new Unsupported(null); }
        var (_, path) = Full(roots, folder);
        if (!Directory.Exists(path)) { throw Exists(path) ? new Unsupported(id) : new NotFound(id); }
        return path;
    }

    private string Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SafeRelativePath.TryParse(name, out var safe) || safe.Value.Contains('/'))
        { throw new ArgumentException("The storage plan is invalid."); }
        if (name.EndsWith(PartialSuffix, PathComparison)
            || name.Equals(_shape.TrashFolderName, PathComparison)
            || _shape.HiddenName?.Invoke(name) == true
            || name.EndsWith('.')
            || name.EndsWith(' ')
            || name.Any(static c => c < 32 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal)))
        { throw new Denied(null); }
        return safe.Value;
    }

    private static string Place(string destination, ConflictPolicy policy, string? source, ItemId? id)
    {
        if (source is not null && string.Equals(source, destination, PathComparison)) { return destination; }
        if (!Exists(destination)) { return destination; }
        if (policy == ConflictPolicy.Overwrite) { return destination; }
        if (policy == ConflictPolicy.Fail) { throw new Conflict(id); }
        var directory = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!Exists(candidate)) { return candidate; }
        }
        throw new Conflict(id);
    }

    private void Trash(IReadOnlyList<LocalRoot> roots, string source)
    {
        var root = RootContaining(roots, source);
        var trash = Path.Combine(root.Path, _shape.TrashFolderName);
        PathConfinement.RequireNoLinks(trash);
        Directory.CreateDirectory(trash);
        var target = Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(source));
        MovePath(source, target, overwrite: false);
    }

    private static void DeletePath(string path)
    {
        if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
        else if (File.Exists(path)) { File.Delete(path); }
    }

    private static void CopyPath(string source, string destination, bool overwrite)
    {
        PathConfinement.RequireNoLinks(source);
        if (overwrite && Exists(destination) && !string.Equals(source, destination, PathComparison)) { DeletePath(destination); }
        if (File.Exists(source)) { File.Copy(source, destination, overwrite: false); return; }
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
            CopyPath(entry.FullName, Path.Combine(destination, entry.Name), overwrite: false);
        }
    }

    private static void MovePath(string source, string destination, bool overwrite)
    {
        if (overwrite && Exists(destination) && !string.Equals(source, destination, PathComparison)) { DeletePath(destination); }
        if (Directory.Exists(source)) { Directory.Move(source, destination); }
        else { File.Move(source, destination, overwrite: false); }
    }

    // Each write stages under its own name, so two writes to one file never share (or delete) a staging file.
    private static string Partial(string destination)
        => Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N")[..12] + PartialSuffix);

    private void Walk(IReadOnlyList<LocalRoot> roots, LocalRoot root, string directory, string relative, int depth, string text, HashSet<string> visited, List<Item> found, CancellationToken ct)
    {
        if (_shape.SearchDepth is { } maxDepth && depth > maxDepth) { return; }
        if (_shape.SearchCap is { } cap && found.Count >= cap) { return; }
        ct.ThrowIfCancellationRequested();
        if (!visited.Add(directory)) { return; }
        Recheck(roots, directory, null);
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        foreach (var entry in entries)
        {
            if (!Visible(entry.Name) || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
            ct.ThrowIfCancellationRequested();
            var child = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase) && (_shape.SearchCap is not { } limit || found.Count < limit))
            {
                found.Add(ItemOf(root, child));
            }
            if (entry is DirectoryInfo && (_shape.SearchDepth is not { } depthLimit || depth < depthLimit))
            {
                Walk(roots, root, entry.FullName, child, depth + 1, text, visited, found, ct);
            }
        }
    }

    private Item ItemOf(IReadOnlyList<LocalRoot> roots, LocalRoot root, FileSystemInfo entry)
    {
        var folder = entry is DirectoryInfo;
        var flags = folder
            ? IsRoot(roots, entry.FullName) ? ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder : Offered
            : Offered & ~(ItemFlags.Search | ItemFlags.CreateFolder);
        long? size = entry is FileInfo file ? file.Length : null;
        var name = folder ? Name(entry.FullName) : entry.Name;
        return new Item(_codec.Encode(root, entry.FullName), name, folder ? ItemKind.Folder : ItemKind.File, size, entry.LastWriteTimeUtc, flags);
    }

    private Item ItemOf(LocalRoot root, string relative)
    {
        var path = relative.Length == 0 ? root.Path : Join(root.Path, relative);
        var folder = Directory.Exists(path);
        var flags = relative.Length == 0
            ? ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder
            : folder ? Offered : Offered & ~(ItemFlags.Search | ItemFlags.CreateFolder);
        long? size = !folder && File.Exists(path) ? new FileInfo(path).Length : null;
        DateTimeOffset? modified = Directory.Exists(path) || File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        var name = relative.Length == 0 ? Name(root.Path) : Path.GetFileName(path);
        return new Item(_codec.Encode(root, path), name, folder ? ItemKind.Folder : ItemKind.File, size, modified, flags);
    }

    internal static LocalRoot RootContaining(IReadOnlyList<LocalRoot> roots, string full)
    {
        LocalRoot? best = null;
        foreach (var root in roots)
        {
            if (!Under(root.Path, full)) { continue; }
            if (best is null || root.Path.Length > best.Path.Length) { best = root; }
        }
        return best ?? throw new Denied(null);
    }

    private static bool IsRoot(IReadOnlyList<LocalRoot> roots, string path)
    {
        var normalized = PathConfinement.Normalize(path);
        return roots.Any(root => string.Equals(PathConfinement.Normalize(root.Path), normalized, PathComparison));
    }

    internal static bool Under(string root, string full)
    {
        if (string.Equals(root, full, PathComparison)) { return true; }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, PathComparison);
    }

    // Only the segments under the root count: the root's own path may hold a dot folder (a home folder such as .intochat).
    private bool IsPrivateOrHidden(string root, string full, out bool isPrivate)
    {
        foreach (var part in Relative(root, full).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsPrivateName(part)) { isPrivate = true; return true; }
            if (_shape.HiddenName?.Invoke(part) == true) { isPrivate = false; return true; }
        }
        isPrivate = false;
        return false;
    }

    private bool IsPrivateName(string name)
        => name.Equals(_shape.TrashFolderName, PathComparison) || name.EndsWith(PartialSuffix, PathComparison);

    private bool Visible(string name)
        => !IsPrivateName(name) && _shape.HiddenName?.Invoke(name) != true;

    // The cap counts what is on disk under the roots, the trash included (it is still the brain's bytes).
    private void RequireRoom(IReadOnlyList<LocalRoot> roots, long adding, long replaced, ItemId? id)
    {
        if (_shape.MaxStorageBytes is not { } max) { return; }
        if (UsedBytes(roots) - replaced + adding > max) { throw new QuotaExceeded(id); }
    }

    private static long UsedBytes(IReadOnlyList<LocalRoot> roots)
    {
        long used = 0;
        foreach (var root in roots.Where(root => Directory.Exists(root.Path)))
        {
            foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                if (!file.Name.EndsWith(PartialSuffix, PathComparison)) { used += file.Length; }
            }
        }
        return used;
    }

    private static long SizeOf(string path)
        => File.Exists(path) ? new FileInfo(path).Length
            : new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(file => file.Length);

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Join(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) { return root; }
        return SafeRelativePath.Parse(relative).ResolveUnder(root);
    }

    internal static string Relative(string root, string full)
    {
        if (string.Equals(root, full, PathComparison)) { return ""; }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var rel = full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
        return SafeRelativePath.TryParse(rel, out var safe) ? safe.Value : rel;
    }

    private static string Name(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;

    private static string ETag(FileInfo file) => string.Create(CultureInfo.InvariantCulture, $"{file.LastWriteTimeUtc.Ticks:x}-{file.Length:x}");

    private static string ContentType(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".TXT" or ".LOG" or ".MD" or ".CSV" => "text/plain",
        ".JSON" => "application/json",
        ".HTML" or ".HTM" => "text/html",
        ".PDF" => "application/pdf",
        ".PNG" => "image/png",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".GIF" => "image/gif",
        _ => "application/octet-stream",
    };

    private static List<Item> Sorted(IEnumerable<Item> items, DigitalBrain.Capabilities.Storage.SortKey sort, bool descending)
    {
        var ordered = sort switch
        {
            DigitalBrain.Capabilities.Storage.SortKey.Name => items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            DigitalBrain.Capabilities.Storage.SortKey.Modified => items.OrderBy(item => item.Modified),
            DigitalBrain.Capabilities.Storage.SortKey.Size => items.OrderBy(item => item.Size ?? -1),
            _ => items.OrderBy(item => item.Kind == ItemKind.Folder ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = ordered.ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
        if (descending) { list.Reverse(); }
        return list;
    }

    private static Page<Item> Cut(List<Item> all, string? cursor, int pageSize)
    {
        var offset = 0;
        if (cursor is { Length: > 0 })
        {
            try { offset = int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None, CultureInfo.InvariantCulture); }
            catch (FormatException) { offset = 0; }
            catch (OverflowException) { offset = 0; }
            offset = Math.Clamp(offset, 0, all.Count);
        }
        var end = Math.Min(all.Count, offset + pageSize);
        return new Page<Item>([.. all.Skip(offset).Take(end - offset)],
            end < all.Count ? Convert.ToBase64String(Encoding.UTF8.GetBytes(end.ToString(CultureInfo.InvariantCulture))) : null);
    }

    private static string IoCode(IOException error) => (error.HResult & 0xffff) is 0x70 or 0x27 ? "quota-exceeded" : "unavailable";

    private static T Guard<T>(ItemId? id, Func<T> body)
    {
        try { return body(); }
        catch (StorageException) { throw; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { throw new NotFound(id, error); }
        catch (UnauthorizedAccessException error) { throw new Denied(id, error); }
        catch (IOException error) when (IoCode(error) == "quota-exceeded") { throw new QuotaExceeded(id, error); }
        catch (IOException error) { throw new Unavailable(id, error); }
    }

    private static void Cleanup(string stage)
    {
        try { if (File.Exists(stage)) { File.Delete(stage); } }
        catch (IOException error) { OsConsoleLog.For<ConfinedDiskStorage>().LogSwallowed(error, "Storage staging cleanup could not finish."); }
        catch (UnauthorizedAccessException error) { OsConsoleLog.For<ConfinedDiskStorage>().LogSwallowed(error, "Storage staging cleanup could not finish."); }
    }

    private sealed class WriteHandle(ConfinedDiskStorage owner, WritePlan plan, string stage, Stream stream) : StorageWriteStream(stream)
    {
        protected override async Task<ItemId> CommitCore(CancellationToken ct)
        {
            try
            {
                await Content.FlushAsync(ct).ConfigureAwait(false);
                await Content.DisposeAsync().ConfigureAwait(false);
                return await owner.Commit(stage, plan, ct).ConfigureAwait(false);
            }
            finally { Cleanup(stage); }
        }

        protected override async Task AbortCore(CancellationToken ct)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
            Cleanup(stage);
        }
    }

    private sealed record ConfinedDiskOperation(WritePlan Plan, long? Bytes, string? Sha256) : OperationPlan(Plan.DryRun);

    private sealed class BoundedWriteStream(Stream inner, long maxBytes, ItemId? targetFolder) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_written + count > maxBytes) { throw new QuotaExceeded(targetFolder); }
            inner.Write(buffer, offset, count);
            _written += count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_written + buffer.Length > maxBytes) { throw new QuotaExceeded(targetFolder); }
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) { return 0; }
            var read = inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left <= 0) { return 0; }
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
            _left -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
