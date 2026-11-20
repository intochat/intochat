using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.LocalDisk;
using IntoChat.OS.Framework;
using IntoChat.OS.Primitives;
using DigitalBrain.Log;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

public interface IFilesVfsService
{
    string BrainId { get; }
    Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string? query = null, CancellationToken ct = default);
    Task<WorkspaceFileEntry?> ResolveFileAsync(string? nameOrUriOrPath, CancellationToken ct = default);
    Task<string?> ReadTextAsync(string nameOrUriOrPath, CancellationToken ct = default);
    Task<byte[]?> ReadBytesAsync(string nameOrUriOrPath, CancellationToken ct = default);
    Task<WorkspaceFileEntry> WriteFileAsync(string fileNameOrPath, ReadOnlyMemory<byte> content, CancellationToken ct = default);
    Task<WorkspaceFileEntry> WriteTextAsync(string fileNameOrPath, string content, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string fileNameOrPath, CancellationToken ct = default) => Task.FromResult(false);
}

public interface IWithFilesVfs
{
    IFilesVfsService? FilesVfs { get; set; }
}

public sealed class WorkspaceFilesService : IFilesVfsService
{
    private static readonly string[] CanonicalMounts = ["work", "local", "drive"];

    private readonly IServiceProvider? _services;
    private readonly ICapabilityBroker? _broker;
    private readonly string _rootDirectory;
    private readonly LocalCloudStorage _fallbackWorkStorage;
    private readonly LocalCloudStorage _fallbackLocalStorage;
    private readonly LocalCloudStorage _fallbackDriveStorage;

    public WorkspaceFilesService(
        IServiceProvider? services = null,
        string? brainId = null,
        ICapabilityBroker? broker = null,
        string? rootDirectory = null)
    {
        _services = services;
        _broker = broker;
        BrainId = string.IsNullOrWhiteSpace(brainId) ? Guid.NewGuid().ToString("N") : brainId.Trim();
        _rootDirectory = !string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.GetFullPath(rootDirectory)
            : Path.Combine(Path.GetTempPath(), "intochat-vfs", BrainId);

        var workDir = Path.Combine(_rootDirectory, "work");
        var localDir = Path.Combine(_rootDirectory, "local");
        var driveDir = Path.Combine(_rootDirectory, "drive");

        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(localDir);
        Directory.CreateDirectory(driveDir);

        _fallbackWorkStorage = new LocalCloudStorage(workDir);
        _fallbackLocalStorage = new LocalCloudStorage(localDir);
        _fallbackDriveStorage = new LocalCloudStorage(driveDir);
    }

    public string BrainId { get; }
    public const long MaxReadBytes = 16 * 1024 * 1024;

    public static WorkspaceFilesService ForBrain(
        IServiceProvider? services = null,
        string? brainId = null,
        ICapabilityBroker? broker = null,
        string? rootDirectory = null)
        => new(services, brainId, broker, rootDirectory);

    public static LocalCloudStorage GetFallbackWorkspaceStorage(string brainId, string? rootDirectory = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.GetFullPath(rootDirectory)
            : Path.Combine(Path.GetTempPath(), "intochat-vfs", NormalizeBrainId(brainId));
        var workDir = Path.Combine(baseDir, "work");
        Directory.CreateDirectory(workDir);
        return new LocalCloudStorage(workDir);
    }

    public static LocalCloudStorage GetFallbackLocalStorage(string brainId, string? rootDirectory = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.GetFullPath(rootDirectory)
            : Path.Combine(Path.GetTempPath(), "intochat-vfs", NormalizeBrainId(brainId));
        var localDir = Path.Combine(baseDir, "local");
        Directory.CreateDirectory(localDir);
        return new LocalCloudStorage(localDir);
    }

    public static LocalCloudStorage GetFallbackDriveStorage(string brainId, string? rootDirectory = null)
    {
        var baseDir = !string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.GetFullPath(rootDirectory)
            : Path.Combine(Path.GetTempPath(), "intochat-vfs", NormalizeBrainId(brainId));
        var driveDir = Path.Combine(baseDir, "drive");
        Directory.CreateDirectory(driveDir);
        return new LocalCloudStorage(driveDir);
    }

    public async Task<IStorageVfs> BuildVfsAsync(ICapabilityBroker? effectiveBroker, CancellationToken ct = default)
    {
        IReadOnlyList<CapabilityConnection<IStorage>> connections = [];
        if (effectiveBroker is not null)
        {
            try { connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        var workStorage = ResolveMountStorage(connections, ["cloud", "work"], LocalCloudNeuron.TypeName, fallbackFirstConnection: false)
            ?? _fallbackWorkStorage;
        var localStorage = ResolveMountStorage(connections, ["local-disk", "disk"], "local-disk", fallbackFirstConnection: false)
            ?? _fallbackLocalStorage;
        var driveStorage = ResolveMountStorage(connections, ["drive", "blob", "google"], neuronType: null, fallbackFirstConnection: false)
            ?? _fallbackDriveStorage;

        return new StorageVfs(
        [
            new VfsMount("work", "Workspace", workStorage),
            new VfsMount("local", "Local Disk", localStorage),
            new VfsMount("drive", "Drive", driveStorage),
        ]);
    }

    private IStorage? ResolveMountStorage(
        IReadOnlyList<CapabilityConnection<IStorage>> connections,
        string[] keyTokens,
        string? neuronType,
        bool fallbackFirstConnection)
    {
        if (connections.FirstOrDefault(c => keyTokens.Any(t => c.ConnectionKey.Contains(t, StringComparison.OrdinalIgnoreCase))) is { } matched)
        {
            return matched.Capability;
        }

        if (_services is not null && !string.IsNullOrWhiteSpace(neuronType))
        {
            try { return SiloNeurons.Get<IStorage>(_services, new NeuronId(new BrainId(BrainId), neuronType, "main")); }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        return fallbackFirstConnection && connections.Count > 0 ? connections[0].Capability : null;
    }

    // The brain's mounts addressed by exact path: no fallback to another mount and no match by file name. File shares use
    // this, because a request under a share must reach that path or nothing.
    public Task<T> WithVfs<T>(Func<IStorageVfs, Task<T>> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return WithCallerAsync(async effectiveBroker => await work(await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false)).ConfigureAwait(false));
    }

    public static string ToCanonicalVfsPath(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) { return "/work/artifact.bin"; }
        if (fileNameOrPath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Null byte is not allowed in file path.", nameof(fileNameOrPath));
        }

        var normalized = fileNameOrPath.Trim().Trim('"', '\'', '`').Replace('\\', '/');

        if (normalized.StartsWith('/'))
        {
            var trimmedLeading = normalized.TrimStart('/');
            var segments = trimmedLeading.Split('/', 2);
            var mount = segments[0].ToLowerInvariant();
            if (mount is "work" or "local" or "drive")
            {
                if (segments.Length == 1 || string.IsNullOrWhiteSpace(segments[1]))
                {
                    return $"/{mount}";
                }
                var subPath = segments[1];
                if (!SafeRelativePath.TryParse(subPath, out var safeSub))
                {
                    throw new ArgumentException($"Invalid or unsafe relative path '{subPath}' under mount '/{mount}'.", nameof(fileNameOrPath));
                }
                return $"/{mount}/{safeSub.Value}";
            }

            if (!SafeRelativePath.TryParse(trimmedLeading, out var safeRelative))
            {
                throw new ArgumentException($"Invalid or unsafe path '{fileNameOrPath}'.", nameof(fileNameOrPath));
            }
            return $"/work/{safeRelative.Value}";
        }

        if (normalized.Contains("neuron://", StringComparison.OrdinalIgnoreCase))
        {
            var clean = ExtractCleanFileName(normalized);
            if (string.IsNullOrWhiteSpace(clean) || !SafeRelativePath.TryParse(clean, out var safeClean))
            {
                throw new ArgumentException($"Invalid or unsafe file name from neuron URI '{fileNameOrPath}'.", nameof(fileNameOrPath));
            }
            return $"/work/{safeClean.Value}";
        }

        if (!SafeRelativePath.TryParse(normalized, out var safePath))
        {
            throw new ArgumentException($"Invalid or unsafe relative path '{fileNameOrPath}'.", nameof(fileNameOrPath));
        }

        return $"/work/{safePath.Value}";
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string? query = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var byName = new Dictionary<string, WorkspaceFileEntry>(StringComparer.OrdinalIgnoreCase);

        await WithCallerAsync(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            foreach (var mount in CanonicalMounts)
            {
                try
                {
                    foreach (var item in await vfs.List($"/{mount}", ct).ConfigureAwait(false))
                    {
                        AddOrPreferNewest(byName, MapVfsItem(mount, item));
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
            }

            if (effectiveBroker is not null)
            {
                try
                {
                    foreach (var conn in await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false))
                    {
                        await CollectConnectionEntriesAsync(conn, byName, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
            }
            return true;
        }).ConfigureAwait(false);

        foreach (var (mount, storage) in new[] { ("work", _fallbackWorkStorage), ("local", _fallbackLocalStorage), ("drive", _fallbackDriveStorage) })
        {
            try
            {
                var roots = await storage.Roots(ct).ConfigureAwait(false);
                foreach (var root in roots)
                {
                    var page = await storage.List(root.Id, new ListQuery(PageSize: 100), ct).ConfigureAwait(false);
                    foreach (var item in page.Items)
                    {
                        if (item.Kind != ItemKind.Folder)
                        {
                            var connKey = mount == "work" ? "local-disk" : mount;
                            AddOrPreferNewest(byName, new WorkspaceFileEntry(
                                Name: item.Name,
                                Connection: connKey,
                                ItemId: $"/{mount}/{item.Name}",
                                Kind: "file",
                                Size: item.Size,
                                NeuronUri: BuildNeuronUri(BrainId, connKey, item.Name),
                                VfsPath: $"/{mount}/{item.Name}",
                                Modified: item.Modified));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
        }

        IEnumerable<WorkspaceFileEntry> results = byName.Values;
        if (trimmedQuery is not null)
        {
            results = results.Where(entry =>
                entry.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.VfsPath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.NeuronUri.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));
        }

        return [.. results
            .OrderByDescending(e => e.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task CollectConnectionEntriesAsync(
        CapabilityConnection<IStorage> conn,
        Dictionary<string, WorkspaceFileEntry> byName,
        CancellationToken ct)
    {
        try
        {
            foreach (var root in await conn.Capability.Roots(ct).ConfigureAwait(false))
            {
                if (root.Kind != ItemKind.Folder)
                {
                    AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, root));
                    continue;
                }
                try
                {
                    var children = await conn.Capability.List(root.Id, new(PageSize: 100), ct).ConfigureAwait(false);
                    foreach (var child in children.Items) { AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, child)); }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
    }

    public async Task<WorkspaceFileEntry?> ResolveFileAsync(string? nameOrUriOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath)) { return null; }

        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);
        var cleanName = ExtractCleanFileName(canonicalPath);

        var vfsEntry = await WithCallerAsync<WorkspaceFileEntry?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            foreach (var candidatePath in new[] { canonicalPath, $"/work/{cleanName}", $"/local/{cleanName}", $"/drive/{cleanName}" })
            {
                try
                {
                    if (await vfs.Stat(candidatePath, ct).ConfigureAwait(false) is { Kind: not ItemKind.Folder } item)
                    {
                        var mount = candidatePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "work";
                        return MapVfsItem(mount, item);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
            }
            return null;
        }).ConfigureAwait(false);

        if (vfsEntry is not null) { return vfsEntry; }

        foreach (var (mount, storage) in new[] { ("work", _fallbackWorkStorage), ("local", _fallbackLocalStorage), ("drive", _fallbackDriveStorage) })
        {
            try
            {
                var item = await storage.Stat(new ItemId(cleanName), ct).ConfigureAwait(false);
                if (item is { Kind: not ItemKind.Folder })
                {
                    return MapItem(mount == "work" ? "local-disk" : mount, item);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        var cleanSlug = SlugSegment(cleanName);
        var searched = await ListFilesAsync(cleanName, ct).ConfigureAwait(false);
        return FindBestMatch(searched, cleanName, cleanSlug, nameOrUriOrPath)
            ?? FindBestMatch(await ListFilesAsync(null, ct).ConfigureAwait(false), cleanName, cleanSlug, nameOrUriOrPath);
    }

    public async Task<string?> ReadTextAsync(string nameOrUriOrPath, CancellationToken ct = default)
    {
        var bytes = await ReadBytesAsync(nameOrUriOrPath, ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]?> ReadBytesAsync(string nameOrUriOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath)) { return null; }

        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);
        var cleanName = ExtractCleanFileName(canonicalPath);

        var vfsBytes = await WithCallerAsync<byte[]?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            foreach (var candidatePath in new[] { canonicalPath, $"/work/{cleanName}", $"/local/{cleanName}", $"/drive/{cleanName}" })
            {
                try
                {
                    var read = await vfs.Read(candidatePath, new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                    if (read is not null && (read.Bytes.Length > 0 || read.TotalLength == 0))
                    {
                        return read.Bytes.ToArray();
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
            }
            return null;
        }).ConfigureAwait(false);

        if (vfsBytes is not null) { return vfsBytes; }

        foreach (var storage in new[] { _fallbackWorkStorage, _fallbackLocalStorage, _fallbackDriveStorage })
        {
            try
            {
                var content = await storage.Read(new ItemId(cleanName), new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                if (content is not null && (content.Bytes.Length > 0 || content.TotalLength == 0))
                {
                    return content.Bytes.ToArray();
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        var resolved = await ResolveFileAsync(nameOrUriOrPath, ct).ConfigureAwait(false);
        if (resolved is null) { return null; }

        return await WithCallerAsync<byte[]?>(async effectiveBroker =>
        {
            if (effectiveBroker is null) { return null; }
            try
            {
                var connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
                var targetConn = connections.FirstOrDefault(c => string.Equals(c.ConnectionKey, resolved.Connection, StringComparison.OrdinalIgnoreCase))
                    ?? connections.FirstOrDefault();
                if (targetConn is null) { return null; }
                return (await targetConn.Capability.Read(new ItemId(resolved.ItemId), new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false)).Bytes.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
                return null;
            }
        }).ConfigureAwait(false);
    }

    public async Task<WorkspaceFileEntry> WriteFileAsync(
        string fileNameOrPath,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var canonicalPath = ToCanonicalVfsPath(fileNameOrPath);
        var cleanName = ExtractCleanFileName(canonicalPath);
        if (string.IsNullOrWhiteSpace(cleanName)) { cleanName = "artifact.bin"; }

        var bytes = content.ToArray();
        var mountPoint = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "work";
        var connectionKey = mountPoint == "work" ? "local-disk" : mountPoint;

        var entry = new WorkspaceFileEntry(
            Name: cleanName,
            Connection: connectionKey,
            ItemId: canonicalPath,
            Kind: "file",
            Size: bytes.LongLength,
            NeuronUri: BuildNeuronUri(BrainId, connectionKey, cleanName),
            VfsPath: canonicalPath,
            Modified: DateTimeOffset.UtcNow);

        await WithCallerAsync(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            try { await vfs.Write(canonicalPath, bytes, ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }

            try
            {
                var targetStorage = mountPoint switch
                {
                    "local" => _fallbackLocalStorage,
                    "drive" => _fallbackDriveStorage,
                    _ => _fallbackWorkStorage,
                };
                await targetStorage.Write(WritePlan.WriteFile(new ItemId(""), cleanName, bytes, ConflictPolicy.Overwrite), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            if (effectiveBroker is not null && mountPoint == "work")
            {
                try
                {
                    foreach (var conn in await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false))
                    {
                        if (conn.ConnectionKey.Contains("local-disk", StringComparison.OrdinalIgnoreCase)
                            || conn.ConnectionKey.Contains("disk", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        try
                        {
                            var roots = await conn.Capability.Roots(ct).ConfigureAwait(false);
                            if (roots.FirstOrDefault(r => r.Flags.HasFlag(ItemFlags.Write)) is { } writableRoot)
                            {
                                await conn.Capability.Write(WritePlan.WriteFile(writableRoot.Id, cleanName, bytes, ConflictPolicy.Overwrite), ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
            }
            return true;
        }).ConfigureAwait(false);

        await TryRefreshFilesWorkspaceAsync(ct).ConfigureAwait(false);
        return entry;
    }

    public Task<WorkspaceFileEntry> WriteTextAsync(string fileNameOrPath, string content, CancellationToken ct = default)
        => WriteFileAsync(fileNameOrPath, Encoding.UTF8.GetBytes(content ?? string.Empty), ct);

    public async Task<bool> DeleteFileAsync(string fileNameOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var canonicalPath = ToCanonicalVfsPath(fileNameOrPath);
        var cleanName = ExtractCleanFileName(canonicalPath);

        var mount = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "work";
        var relative = canonicalPath.Length > mount.Length + 1 ? canonicalPath.Substring(mount.Length + 1).TrimStart('/') : cleanName;
        var targetStorage = mount switch
        {
            "local" => _fallbackLocalStorage,
            "drive" => _fallbackDriveStorage,
            _ => _fallbackWorkStorage,
        };

        try { await targetStorage.Write(WritePlan.Delete(new ItemId(relative)), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }

        try { await targetStorage.Write(WritePlan.Delete(new ItemId(cleanName)), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }

        await TryRefreshFilesWorkspaceAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task TryRefreshFilesWorkspaceAsync(CancellationToken ct)
    {
        if (_services is null) { return; }
        try { await SiloNeurons.Get<IFilesWorkspace>(_services, IntoChat.OS.Primitives.AppRef.Of(new(BrainId), "files").ChildKey("workspace")).Open(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
    }

    private async Task<T> WithCallerAsync<T>(Func<ICapabilityBroker?, Task<T>> action)
    {
        var resolvedBroker = _broker ?? _services?.GetService<ICapabilityBroker>();
        var filesAppKey = IntoChat.OS.Primitives.AppRef.Of(new DigitalBrain.BrainId(BrainId), "files").Key;
        var effectiveBroker = resolvedBroker is CapabilityBroker cb ? cb.ForApp(filesAppKey) : resolvedBroker;

        var edge = _services?.GetService<DigitalBrain.Access.EdgeAuthority>();
        using var scope = edge?.EnterInProcess();
        return await action(effectiveBroker).ConfigureAwait(false);
    }

    private WorkspaceFileEntry MapVfsItem(string connection, VfsItem item)
    {
        var connKey = string.IsNullOrWhiteSpace(connection) || string.Equals(connection, "work", StringComparison.OrdinalIgnoreCase)
            ? "local-disk"
            : connection;
        return new WorkspaceFileEntry(
            Name: item.Name,
            Connection: connKey,
            ItemId: item.Path,
            Kind: item.Kind == ItemKind.Folder ? "folder" : "file",
            Size: item.Size,
            NeuronUri: BuildNeuronUri(BrainId, connKey, item.Name),
            VfsPath: item.Path,
            Modified: item.Modified);
    }

    private WorkspaceFileEntry MapItem(string connection, Item item)
    {
        var rawName = string.IsNullOrWhiteSpace(item.Name) ? item.Id.Value : item.Name.Trim();
        var fileName = Path.GetFileName(rawName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName)) { fileName = rawName; }

        var connKey = string.IsNullOrWhiteSpace(connection) ? "local-disk" : connection;
        var kind = item.Kind == ItemKind.Folder ? "folder" : item.Kind == ItemKind.Link ? "link" : "file";
        return new WorkspaceFileEntry(
            Name: fileName,
            Connection: connKey,
            ItemId: item.Id.Value,
            Kind: kind,
            Size: item.Size,
            NeuronUri: BuildNeuronUri(BrainId, connKey, fileName),
            VfsPath: rawName.StartsWith('/') ? rawName : $"/work/{fileName}",
            Modified: item.Modified);
    }

    private static void AddOrPreferNewest(Dictionary<string, WorkspaceFileEntry> byName, WorkspaceFileEntry candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name)) { return; }
        if (!byName.TryGetValue(candidate.Name, out var existing)
            || (candidate.Modified ?? DateTimeOffset.MinValue) >= (existing.Modified ?? DateTimeOffset.MinValue))
        {
            byName[candidate.Name] = candidate;
        }
    }

    private static bool IsMatchingEntry(WorkspaceFileEntry entry, string cleanName, string cleanSlug, string canonicalPath, string rawQuery)
        => string.Equals(entry.Name, cleanName, StringComparison.OrdinalIgnoreCase)
           || string.Equals(entry.VfsPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
           || string.Equals(entry.VfsPath, rawQuery.Trim(), StringComparison.OrdinalIgnoreCase)
           || string.Equals(entry.NeuronUri, rawQuery.Trim(), StringComparison.OrdinalIgnoreCase)
           || (cleanSlug.Length > 0 && string.Equals(SlugSegment(entry.Name), cleanSlug, StringComparison.OrdinalIgnoreCase));

    private static WorkspaceFileEntry? FindBestMatch(
        IReadOnlyList<WorkspaceFileEntry> candidates,
        string cleanName,
        string cleanSlug,
        string rawQuery)
    {
        var canonicalPath = ToCanonicalVfsPath(rawQuery);
        return candidates.FirstOrDefault(entry => IsMatchingEntry(entry, cleanName, cleanSlug, canonicalPath, rawQuery))
            ?? candidates.FirstOrDefault(e => e.Kind != "folder" && e.Name.Contains(cleanName, StringComparison.OrdinalIgnoreCase));
    }

    public static string ExtractCleanFileName(string? nameOrUriOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath)) { return string.Empty; }

        var trimmed = nameOrUriOrPath.Trim().Trim('"', '\'', '`', '(', ')', '[', ']', ';', ',');
        if (trimmed.StartsWith('@')) { trimmed = trimmed[1..].Trim(); }

        var neuronIdx = trimmed.IndexOf("neuron://", StringComparison.OrdinalIgnoreCase);
        if (neuronIdx >= 0)
        {
            var endIdx = trimmed.IndexOfAny([' ', ')', '\r', '\n', ']'], neuronIdx);
            var uriSlice = endIdx > neuronIdx ? trimmed[neuronIdx..endIdx] : trimmed[neuronIdx..];
            var lastSlash = uriSlice.LastIndexOf('/');
            var candidate = lastSlash >= 0 && lastSlash + 1 < uriSlice.Length ? uriSlice[(lastSlash + 1)..] : uriSlice;
            return SafeRelativePath.TryParse(candidate, out var safeCandidate) ? safeCandidate.Value : candidate;
        }

        var normalized = trimmed.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var rawName = string.IsNullOrWhiteSpace(fileName) ? normalized.Trim('/') : fileName;
        return SafeRelativePath.TryParse(rawName, out var safe) ? safe.Value : rawName;
    }

    internal static string BuildNeuronUri(string brainId, string connection, string name)
    {
        var effectiveBrain = string.IsNullOrWhiteSpace(brainId) ? "brain" : brainId.Trim();
        return $"neuron://{effectiveBrain}/{SlugSegment(string.IsNullOrWhiteSpace(connection) ? "local-disk" : connection)}.files/{SlugSegment(name)}";
    }

    internal static string SlugSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "file"; }
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_') { builder.Append(ch); }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\') { builder.Append('-'); }
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length > 0 ? slug : "file";
    }

    private static string NormalizeBrainId(string? brainId)
    {
        if (string.IsNullOrWhiteSpace(brainId))
        {
            throw new ArgumentException("A valid non-empty brain id is required.", nameof(brainId));
        }
        return brainId.Trim();
    }

    private static void LogRecoverable(Exception error) =>
        OsConsoleLog.For<WorkspaceFilesService>().LogSwallowed(error, "Recoverable workspace files VFS error.");
}
