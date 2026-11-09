using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;

namespace IntoChat.OS.Authority;

// OS module 'cloud': filtered local cloud storage over LocalCloudNeuron.
[Module(Id)]
public sealed class CloudModule : Module
{
    public const string Id = "cloud";

    public static ModuleManifest Manifest { get; } = new(
        Provides: [Capability.Storage.V1],
        Requires: [],
        Needs: [],
        Settings: [],
        Secrets: []);
}

public sealed class LocalCloudState
{
    public string? RootDirectory { get; set; }
    public long MaxStorageBytes { get; set; } = LocalCloudNeuron.DefaultMaxStorageBytes;
    public Dictionary<string, LocalCloudEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed record LocalCloudEntry(
    string Path,
    string Name,
    ItemKind Kind,
    byte[] Content,
    DateTimeOffset Modified,
    string ContentType = "application/octet-stream");

// Filtered local IStorage neuron for the 'cloud' OS module.
// Exposes safe workspace/cloud roots, filters out hidden/dot files (.git, .env, .vs, secrets) and blocks any '..' path traversal.
[Neuron(TypeName)]
public sealed class LocalCloudNeuron : Neuron<LocalCloudState>, IStorage
{
    public const string TypeName = "local-cloud";
    public const string RootIdValue = "cloud";
    public const string RootDisplayName = "Local Cloud";
    public const long DefaultMaxStorageBytes = 1024L * 1024 * 1024; // 1 GiB per brain

    private const ItemFlags Offered = ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder
        | ItemFlags.Copy | ItemFlags.Move | ItemFlags.Rename | ItemFlags.Delete;

    private static readonly HashSet<string> BlockedSegmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".env",
        ".vs",
        ".vscode",
        ".idea",
        ".ssh",
        ".aws",
        ".azure",
        ".npmrc",
        ".pypirc",
        ".DS_Store",
        "secrets",
        "secrets.json",
        "appsettings.secrets.json",
    };

    private static readonly string[] BlockedExtensions =
    [
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".kdbx",
        ".env",
    ];

    private readonly LocalCloudState _standaloneState = new();

    public LocalCloudNeuron()
    {
    }

    public LocalCloudNeuron(string? rootDirectory)
    {
        ConfigureRootDirectory(rootDirectory);
    }

    public long MaxStorageBytes
    {
        get => ActiveState.MaxStorageBytes;
        set => ActiveState.MaxStorageBytes = value;
    }

    private LocalCloudState ActiveState
    {
        get
        {
            try
            {
                return State ?? _standaloneState;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _ = error.Message;
                return _standaloneState;
            }
        }
    }

    public void ConfigureRootDirectory(string? rootDirectory)
        => ActiveState.RootDirectory = !string.IsNullOrWhiteSpace(rootDirectory) ? Path.GetFullPath(rootDirectory) : null;

    public static bool IsTraversalOrInvalid(string? path)
    {
        if (path is null) { return true; }
        if (path.Length == 0) { return false; }
        if (path.Contains('\0', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)) { return true; }
        if (path.StartsWith('/') || path.StartsWith('\\')) { return true; }
        var normalized = path.Replace('\\', '/');
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.Contains("..", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsHiddenOrSecretSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) { return false; }
        var trimmed = segment.Trim();
        if (trimmed.StartsWith('.')) { return true; }
        if (BlockedSegmentNames.Contains(trimmed)) { return true; }
        foreach (var ext in BlockedExtensions)
        {
            if (trimmed.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    public static bool IsFilteredPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) { return false; }
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || string.Equals(normalized, RootIdValue, StringComparison.Ordinal)) { return false; }
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsHiddenOrSecretSegment(segment)) { return true; }
        }
        return false;
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var used = ActiveState.Entries.Values.Sum(e => e.Content.LongLength);
        var limit = ActiveState.MaxStorageBytes;
        var quota = limit > 0 ? Quota.Known(Math.Min(used, limit), limit) : Quota.Unknown;
        return Task.FromResult(new StorageDescriptor(RootDisplayName, TypeName, quota, Offered));
    }

    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<Item>>([FolderItem("", RootDisplayName)]);
    }

    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var rel = NormalizeAndAuthorizeFolder(folder);
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);

        // 1. Physical root directory if configured
        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physicalFolder = ResolvePhysicalPath(rootDir, rel, folder);
            if (File.Exists(physicalFolder)) { throw new Unsupported(folder); }
            if (Directory.Exists(physicalFolder))
            {
                foreach (var dir in Directory.EnumerateDirectories(physicalFolder))
                {
                    var name = Path.GetFileName(dir);
                    if (IsHiddenOrSecretSegment(name) || IsSystemHidden(dir)) { continue; }
                    var childRel = JoinPath(rel, name);
                    items[name] = FolderItem(childRel, name);
                }
                foreach (var file in Directory.EnumerateFiles(physicalFolder))
                {
                    var name = Path.GetFileName(file);
                    if (IsHiddenOrSecretSegment(name) || IsSystemHidden(file)) { continue; }
                    var info = new FileInfo(file);
                    var childRel = JoinPath(rel, name);
                    items[name] = FileItem(childRel, name, info.Length, info.LastWriteTimeUtc);
                }
            }
            else if (rel.Length > 0 && !HasVirtualFolder(rel))
            {
                throw new NotFound(folder);
            }
        }
        else if (rel.Length > 0 && !HasVirtualFolder(rel))
        {
            if (ActiveState.Entries.TryGetValue(rel, out var existing) && existing.Kind == ItemKind.File)
            {
                throw new Unsupported(folder);
            }
            throw new NotFound(folder);
        }

        // 2. Virtual/state entries
        var prefix = rel.Length == 0 ? "" : rel + "/";
        foreach (var entry in ActiveState.Entries.Values)
        {
            if (IsFilteredPath(entry.Path)) { continue; }
            if (prefix.Length > 0 && !entry.Path.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            var remainder = prefix.Length == 0 ? entry.Path : entry.Path[prefix.Length..];
            if (string.IsNullOrEmpty(remainder)) { continue; }
            var slash = remainder.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                if (IsHiddenOrSecretSegment(entry.Name)) { continue; }
                items[entry.Name] = entry.Kind == ItemKind.Folder
                    ? FolderItem(entry.Path, entry.Name)
                    : FileItem(entry.Path, entry.Name, entry.Content.LongLength, entry.Modified);
            }
            else
            {
                var childFolder = remainder[..slash];
                if (IsHiddenOrSecretSegment(childFolder)) { continue; }
                var childPath = JoinPath(rel, childFolder);
                items.TryAdd(childFolder, FolderItem(childPath, childFolder));
            }
        }

        var sorted = SortItems(items.Values, query.Sort, query.Descending);
        return Task.FromResult(Paginate(sorted, query.Cursor, query.PageSize <= 0 ? 100 : query.PageSize));
    }

    public Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rel = NormalizeAndAuthorizeId(id);
        if (rel.Length == 0)
        {
            return Task.FromResult(FolderItem("", RootDisplayName));
        }

        if (ActiveState.Entries.TryGetValue(rel, out var entry))
        {
            return Task.FromResult(entry.Kind == ItemKind.Folder
                ? FolderItem(entry.Path, entry.Name)
                : FileItem(entry.Path, entry.Name, entry.Content.LongLength, entry.Modified));
        }

        if (HasVirtualFolder(rel))
        {
            return Task.FromResult(FolderItem(rel, NameOf(rel)));
        }

        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physical = ResolvePhysicalPath(rootDir, rel, id);
            if (Directory.Exists(physical) && !IsSystemHidden(physical))
            {
                return Task.FromResult(FolderItem(rel, NameOf(rel)));
            }
            if (File.Exists(physical) && !IsSystemHidden(physical))
            {
                var info = new FileInfo(physical);
                return Task.FromResult(FileItem(rel, NameOf(rel), info.Length, info.LastWriteTimeUtc));
            }
        }

        throw new NotFound(id);
    }

    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rel = NormalizeAndAuthorizeId(id);
        if (rel.Length == 0) { throw new Unsupported(id); }

        byte[] allBytes;
        string contentType;
        if (ActiveState.Entries.TryGetValue(rel, out var entry))
        {
            if (entry.Kind == ItemKind.Folder) { throw new Unsupported(id); }
            allBytes = entry.Content;
            contentType = entry.ContentType;
        }
        else if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physical = ResolvePhysicalPath(rootDir, rel, id);
            if (Directory.Exists(physical)) { throw new Unsupported(id); }
            if (!File.Exists(physical) || IsSystemHidden(physical)) { throw new NotFound(id); }
            allBytes = await File.ReadAllBytesAsync(physical, ct).ConfigureAwait(false);
            contentType = GuessContentType(rel);
        }
        else
        {
            throw new NotFound(id);
        }

        var total = allBytes.LongLength;
        var offset = (int)Math.Clamp(range?.Offset ?? 0, 0, total);
        var length = range?.Length is { } req ? (int)Math.Clamp(req, 0, total - offset) : (int)(total - offset);
        var slice = allBytes.AsSpan(offset, length).ToArray();
        return new Content(slice, contentType, total);
    }

    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        await ApplyWriteAsync(plan, ct).ConfigureAwait(false);
        return new OperationId("local-cloud-" + Guid.NewGuid().ToString("N"));
    }

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ct.ThrowIfCancellationRequested();
        var within = query.Within is { } folder ? NormalizeAndAuthorizeFolder(folder) : "";
        var prefix = within.Length == 0 ? "" : within + "/";
        var matches = new Dictionary<string, Item>(StringComparer.Ordinal);

        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var startDir = ResolvePhysicalPath(rootDir, within, query.Within ?? new ItemId(RootIdValue));
            if (Directory.Exists(startDir))
            {
                SearchPhysicalDirectory(rootDir, startDir, query.Text ?? "", matches);
            }
        }

        foreach (var entry in ActiveState.Entries.Values)
        {
            if (entry.Kind != ItemKind.File || IsFilteredPath(entry.Path)) { continue; }
            if (prefix.Length > 0 && !entry.Path.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
            if (string.IsNullOrEmpty(query.Text) || entry.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            {
                matches[entry.Path] = FileItem(entry.Path, entry.Name, entry.Content.LongLength, entry.Modified);
            }
        }

        var sorted = SortItems(matches.Values, DigitalBrain.Capabilities.Storage.SortKey.FolderThenName, descending: false);
        return Task.FromResult(Paginate(sorted, query.Cursor, query.PageSize <= 0 ? 100 : query.PageSize));
    }

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rel = NormalizeAndAuthorizeId(id);
        if (rel.Length == 0) { return Task.FromResult<IReadOnlyList<Item>>([]); }
        var chain = new List<Item> { FolderItem("", RootDisplayName) };
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var walked = "";
        for (var i = 0; i < parts.Length - 1; i++)
        {
            walked = walked.Length == 0 ? parts[i] : walked + "/" + parts[i];
            chain.Add(FolderItem(walked, parts[i]));
        }
        return Task.FromResult<IReadOnlyList<Item>>(chain);
    }

    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var content = await Read(item, range, ct).ConfigureAwait(false);
        var stat = await Stat(item, ct).ConfigureAwait(false);
        var stream = new MemoryStream(content.Bytes.ToArray(), writable: false);
        return new StorageReadStream(stream, content.Bytes.Length, GuessContentType(stat.Name), stat.Hash, canSeek: true, stat.Modified);
    }

    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        if (plan.Kind != WriteKind.WriteFile || plan.DryRun) { throw new Unsupported(plan.TargetFolder); }
        var folder = NormalizeAndAuthorizeFolder(plan.TargetFolder ?? new ItemId(RootIdValue));
        var name = ValidateSafeName(plan.Name, plan.TargetFolder);
        var destination = JoinPath(folder, name);
        if (plan.Conflict != ConflictPolicy.Overwrite && EntryExists(destination))
        {
            throw new Conflict(plan.TargetFolder);
        }
        return Task.FromResult<StorageWriteStream>(new LocalCloudWriteStream(this, destination, name));
    }

    private async Task ApplyWriteAsync(WritePlan plan, CancellationToken ct)
    {
        if (plan.DryRun) { return; }
        switch (plan.Kind)
        {
            case WriteKind.CreateFolder:
                {
                    var folder = NormalizeAndAuthorizeFolder(plan.TargetFolder ?? new ItemId(RootIdValue));
                    var name = ValidateSafeName(plan.Name, plan.TargetFolder);
                    var dest = JoinPath(folder, name);
                    if (plan.Conflict != ConflictPolicy.Overwrite && EntryExists(dest)) { throw new Conflict(plan.TargetFolder); }
                    await StoreEntryAsync(new LocalCloudEntry(dest, name, ItemKind.Folder, [], DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
                    break;
                }
            case WriteKind.WriteFile:
                {
                    var folder = NormalizeAndAuthorizeFolder(plan.TargetFolder ?? new ItemId(RootIdValue));
                    var name = ValidateSafeName(plan.Name, plan.TargetFolder);
                    var dest = JoinPath(folder, name);
                    if (plan.Conflict != ConflictPolicy.Overwrite && EntryExists(dest)) { throw new Conflict(plan.TargetFolder); }
                    var bytes = plan.Inline?.ToArray() ?? [];
                    await StoreEntryAsync(new LocalCloudEntry(dest, name, ItemKind.File, bytes, DateTimeOffset.UtcNow, GuessContentType(name)), ct).ConfigureAwait(false);
                    break;
                }
            case WriteKind.Delete:
                {
                    if (plan.Source is not { } sourceId) { throw new Unsupported(null); }
                    var source = NormalizeAndAuthorizeId(sourceId);
                    if (source.Length == 0) { throw new Denied(sourceId); }
                    RemoveEntry(source);
                    break;
                }
            case WriteKind.Copy:
            case WriteKind.Move:
                {
                    if (plan.Source is not { } sourceId) { throw new Unsupported(null); }
                    var source = NormalizeAndAuthorizeId(sourceId);
                    if (!ActiveState.Entries.TryGetValue(source, out var existing)) { throw new NotFound(sourceId); }
                    var folder = NormalizeAndAuthorizeFolder(plan.TargetFolder ?? new ItemId(RootIdValue));
                    var dest = JoinPath(folder, existing.Name);
                    await StoreEntryAsync(existing with { Path = dest, Modified = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    if (plan.Kind == WriteKind.Move) { RemoveEntry(source); }
                    break;
                }
            case WriteKind.Rename:
                {
                    if (plan.Source is not { } sourceId) { throw new Unsupported(null); }
                    var source = NormalizeAndAuthorizeId(sourceId);
                    if (!ActiveState.Entries.TryGetValue(source, out var existing)) { throw new NotFound(sourceId); }
                    var name = ValidateSafeName(plan.Name, sourceId);
                    var parent = ParentOf(source);
                    var dest = JoinPath(parent, name);
                    await StoreEntryAsync(existing with { Path = dest, Name = name, Modified = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
                    RemoveEntry(source);
                    break;
                }
            default:
                throw new Unsupported(plan.Source ?? plan.TargetFolder);
        }
    }

    internal async Task StoreEntryAsync(LocalCloudEntry entry, CancellationToken ct)
    {
        var limit = ActiveState.MaxStorageBytes;
        if (limit > 0)
        {
            var currentBytes = ActiveState.Entries.Values
                .Where(e => !string.Equals(e.Path, entry.Path, StringComparison.Ordinal))
                .Sum(e => e.Content.LongLength);
            if (currentBytes + entry.Content.LongLength > limit)
            {
                throw new QuotaExceeded(new ItemId(entry.Path));
            }
        }

        ActiveState.Entries[entry.Path] = entry;
        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physical = ResolvePhysicalPath(rootDir, entry.Path, new ItemId(entry.Path));
            if (entry.Kind == ItemKind.Folder)
            {
                Directory.CreateDirectory(physical);
            }
            else
            {
                var parent = Path.GetDirectoryName(physical);
                if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }
                await File.WriteAllBytesAsync(physical, entry.Content, ct).ConfigureAwait(false);
            }
        }
    }

    private void RemoveEntry(string rel)
    {
        ActiveState.Entries.Remove(rel);
        var prefix = rel + "/";
        foreach (var child in ActiveState.Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            ActiveState.Entries.Remove(child);
        }
        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physical = ResolvePhysicalPath(rootDir, rel, new ItemId(rel));
            if (File.Exists(physical)) { File.Delete(physical); }
            else if (Directory.Exists(physical)) { Directory.Delete(physical, recursive: true); }
        }
    }

    private bool EntryExists(string rel)
    {
        if (ActiveState.Entries.ContainsKey(rel) || HasVirtualFolder(rel)) { return true; }
        if (ActiveState.RootDirectory is { Length: > 0 } rootDir && Directory.Exists(rootDir))
        {
            var physical = ResolvePhysicalPath(rootDir, rel, new ItemId(rel));
            return File.Exists(physical) || Directory.Exists(physical);
        }
        return false;
    }

    private bool HasVirtualFolder(string rel)
    {
        if (rel.Length == 0) { return true; }
        if (ActiveState.Entries.TryGetValue(rel, out var folder) && folder.Kind == ItemKind.Folder) { return true; }
        var prefix = rel + "/";
        return ActiveState.Entries.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizeAndAuthorizeFolder(ItemId folder) => NormalizeAndAuthorizeId(folder);

    private static string NormalizeAndAuthorizeId(ItemId id)
    {
        var raw = id.Value;
        if (raw is null) { throw new Denied(id); }
        if (raw.Length == 0 || string.Equals(raw, RootIdValue, StringComparison.Ordinal)) { return ""; }

        var stripped = raw.StartsWith(RootIdValue + "/", StringComparison.Ordinal)
            ? raw[(RootIdValue.Length + 1)..]
            : raw;

        if (IsTraversalOrInvalid(stripped) || IsFilteredPath(stripped))
        {
            throw new Denied(id);
        }

        return stripped.Replace('\\', '/').Trim('/');
    }

    private static string ValidateSafeName(string? name, ItemId? contextId)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || IsTraversalOrInvalid(name)
            || IsHiddenOrSecretSegment(name))
        {
            throw new Denied(contextId);
        }
        return name.Trim();
    }

    private static string ResolvePhysicalPath(string rootDirectory, string relativePath, ItemId originalId)
    {
        var fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combined = relativePath.Length == 0
            ? fullRoot
            : Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !combined.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new Denied(originalId);
        }
        return combined;
    }

    private static void SearchPhysicalDirectory(string rootDir, string currentDir, string searchText, Dictionary<string, Item> matches)
    {
        foreach (var file in Directory.EnumerateFiles(currentDir))
        {
            var name = Path.GetFileName(file);
            if (IsHiddenOrSecretSegment(name) || IsSystemHidden(file)) { continue; }
            if (searchText.Length == 0 || name.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
                if (!IsFilteredPath(rel))
                {
                    var info = new FileInfo(file);
                    matches[rel] = FileItem(rel, name, info.Length, info.LastWriteTimeUtc);
                }
            }
        }
        foreach (var subDir in Directory.EnumerateDirectories(currentDir))
        {
            var name = Path.GetFileName(subDir);
            if (IsHiddenOrSecretSegment(name) || IsSystemHidden(subDir)) { continue; }
            SearchPhysicalDirectory(rootDir, subDir, searchText, matches);
        }
    }

    private static bool IsSystemHidden(string fullPath)
    {
        try
        {
            var attrs = File.GetAttributes(fullPath);
            return (attrs & FileAttributes.Hidden) != 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _ = error.Message;
            return false;
        }
    }

    private static Item FolderItem(string rel, string name)
        => new(new ItemId(rel.Length == 0 ? RootIdValue : rel), name, ItemKind.Folder, null, null,
            rel.Length == 0 ? ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder : Offered);

    private static Item FileItem(string rel, string name, long? size, DateTimeOffset? modified)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{rel}:{size}:{modified?.UtcTicks}")));
        return new(new ItemId(rel), name, ItemKind.File, size, modified, Offered & ~(ItemFlags.Search | ItemFlags.CreateFolder), Hash: hash);
    }

    private static string JoinPath(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;
    private static string ParentOf(string rel) => rel.LastIndexOf('/') is var idx && idx >= 0 ? rel[..idx] : "";
    private static string NameOf(string rel) => rel.LastIndexOf('/') is var idx && idx >= 0 ? rel[(idx + 1)..] : rel;

    private static string GuessContentType(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".TXT" or ".MD" or ".LOG" or ".CSV" => "text/plain",
        ".JSON" => "application/json",
        _ => "application/octet-stream",
    };

    private static List<Item> SortItems(IEnumerable<Item> items, DigitalBrain.Capabilities.Storage.SortKey sort, bool descending)
    {
        var ordered = sort switch
        {
            DigitalBrain.Capabilities.Storage.SortKey.Name => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            DigitalBrain.Capabilities.Storage.SortKey.Modified => items.OrderBy(i => i.Modified),
            DigitalBrain.Capabilities.Storage.SortKey.Size => items.OrderBy(i => i.Size ?? -1),
            _ => items.OrderBy(i => i.Kind == ItemKind.Folder ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = ordered.ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
        if (descending) { list.Reverse(); }
        return list;
    }

    private static Page<Item> Paginate(List<Item> all, string? cursor, int pageSize)
    {
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            try { offset = int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None, CultureInfo.InvariantCulture); }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                _ = error.Message;
                offset = 0;
            }
            offset = Math.Clamp(offset, 0, all.Count);
        }
        var end = Math.Min(all.Count, offset + pageSize);
        return new Page<Item>(
            [.. all.Skip(offset).Take(end - offset)],
            end < all.Count ? Convert.ToBase64String(Encoding.UTF8.GetBytes(end.ToString(CultureInfo.InvariantCulture))) : null);
    }

    private sealed class LocalCloudWriteStream(LocalCloudNeuron owner, string destination, string name)
        : StorageWriteStream(new MemoryStream())
    {
        protected override async Task<ItemId> CommitCore(CancellationToken ct)
        {
            var bytes = ((MemoryStream)Content).ToArray();
            var entry = new LocalCloudEntry(destination, name, ItemKind.File, bytes, DateTimeOffset.UtcNow, GuessContentType(name));
            await owner.StoreEntryAsync(entry, ct).ConfigureAwait(false);
            return new ItemId(destination);
        }

        protected override Task AbortCore(CancellationToken ct) => Task.CompletedTask;
    }
}
