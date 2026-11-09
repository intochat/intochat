using System.Collections.Concurrent;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;

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
    private static readonly IntoChat.OS.Connections.Caching.BoundedCache<string, LocalCloudNeuron> _sharedFallbackWorkStorages = new(256, StringComparer.OrdinalIgnoreCase);
    private static readonly IntoChat.OS.Connections.Caching.BoundedCache<string, LocalCloudNeuron> _sharedFallbackLocalStorages = new(256, StringComparer.OrdinalIgnoreCase);
    private static readonly IntoChat.OS.Connections.Caching.BoundedCache<string, LocalCloudNeuron> _sharedFallbackDriveStorages = new(256, StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, (WorkspaceFileEntry Entry, byte[] Bytes)> _overlay = new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceProvider? _services;
    private readonly ICapabilityBroker? _broker;

    public WorkspaceFilesService(
        IServiceProvider? services = null,
        string brainId = "local",
        ICapabilityBroker? broker = null)
    {
        _services = services;
        _broker = broker;
        BrainId = string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim();
    }

    public string BrainId { get; }

    public const long MaxReadBytes = 16 * 1024 * 1024;

    public static WorkspaceFilesService ForBrain(
        IServiceProvider? services = null,
        string brainId = "local",
        ICapabilityBroker? broker = null) => new(services, brainId, broker);

    public static LocalCloudNeuron GetFallbackWorkspaceStorage(string brainId)
        => _sharedFallbackWorkStorages.GetOrAdd(string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim(), _ => new LocalCloudNeuron());

    public static LocalCloudNeuron GetFallbackLocalStorage(string brainId)
        => _sharedFallbackLocalStorages.GetOrAdd(string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim(), _ =>
        {
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
                {
                    return new LocalCloudNeuron(cwd);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoverable(ex);
            }
            return new LocalCloudNeuron();
        });

    public static LocalCloudNeuron GetFallbackDriveStorage(string brainId)
        => _sharedFallbackDriveStorages.GetOrAdd(string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim(), _ => new LocalCloudNeuron());

    public async Task<IStorageVfs> BuildVfsAsync(ICapabilityBroker? effectiveBroker, CancellationToken ct = default)
    {
        var mounts = new List<VfsMount>();
        IReadOnlyList<CapabilityConnection<IStorage>> connections = [];

        if (effectiveBroker is not null)
        {
            try
            {
                connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        // 1. Primary workspace storage (/work)
        IStorage? workStorage = null;
        var workConn = connections.FirstOrDefault(c =>
            c.ConnectionKey.Contains("cloud", StringComparison.OrdinalIgnoreCase)
            || c.ConnectionKey.Contains("work", StringComparison.OrdinalIgnoreCase));

        if (workConn is not null)
        {
            workStorage = workConn.Capability;
        }
        else if (_services is not null)
        {
            try
            {
                workStorage = SiloNeurons.Get<IStorage>(_services, new NeuronId(new BrainId(BrainId), LocalCloudNeuron.TypeName, "main"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        if (workStorage is null && connections.Count > 0)
        {
            workStorage = connections.FirstOrDefault()?.Capability;
        }

        workStorage ??= GetFallbackWorkspaceStorage(BrainId);
        mounts.Add(new VfsMount("work", "Workspace", workStorage));

        // 2. Local host disk storage (/local)
        IStorage? localStorage = null;
        var localConn = connections.FirstOrDefault(c =>
            c.ConnectionKey.Contains("local-disk", StringComparison.OrdinalIgnoreCase)
            || c.ConnectionKey.Contains("disk", StringComparison.OrdinalIgnoreCase));

        if (localConn is not null)
        {
            localStorage = localConn.Capability;
        }
        else if (_services is not null)
        {
            try
            {
                localStorage = SiloNeurons.Get<IStorage>(_services, new NeuronId(new BrainId(BrainId), "local-disk", "main"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        localStorage ??= GetFallbackLocalStorage(BrainId);
        mounts.Add(new VfsMount("local", "Local Disk", localStorage));

        // 3. Cloud / shared drive storage (/drive)
        var driveConn = connections.FirstOrDefault(c =>
            c.ConnectionKey.Contains("drive", StringComparison.OrdinalIgnoreCase)
            || c.ConnectionKey.Contains("blob", StringComparison.OrdinalIgnoreCase)
            || c.ConnectionKey.Contains("google", StringComparison.OrdinalIgnoreCase));

        if (driveConn is not null)
        {
            mounts.Add(new VfsMount("drive", "Drive", driveConn.Capability));
        }
        else
        {
            mounts.Add(new VfsMount("drive", "Drive", GetFallbackDriveStorage(BrainId)));
        }

        return new StorageVfs(mounts);
    }

    public static string ToCanonicalVfsPath(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return "/work/artifact.bin";
        }

        var normalized = fileNameOrPath.Trim().Trim('"', '\'', '`').Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            var segments = normalized.TrimStart('/').Split('/', 2);
            var mount = segments[0].ToLowerInvariant();
            if (mount is "work" or "local" or "drive")
            {
                return $"/{mount}/{(segments.Length > 1 ? segments[1] : string.Empty)}".TrimEnd('/');
            }
            return $"/work/{normalized.TrimStart('/')}";
        }

        var clean = ExtractCleanFileName(normalized);
        return $"/work/{clean}";
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string? query = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var byName = new Dictionary<string, WorkspaceFileEntry>(StringComparer.OrdinalIgnoreCase);

        await WithCallerAsync(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);

            // 1. List canonical /work
            try
            {
                var workItems = await vfs.List("/work", ct).ConfigureAwait(false);
                foreach (var item in workItems)
                {
                    AddOrPreferNewest(byName, MapVfsItem("work", item));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            // 2. List canonical /local
            try
            {
                var localItems = await vfs.List("/local", ct).ConfigureAwait(false);
                foreach (var item in localItems)
                {
                    AddOrPreferNewest(byName, MapVfsItem("local", item));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            // 3. List canonical /drive
            try
            {
                var driveItems = await vfs.List("/drive", ct).ConfigureAwait(false);
                foreach (var item in driveItems)
                {
                    AddOrPreferNewest(byName, MapVfsItem("drive", item));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            // 4. Query active capability connections
            if (effectiveBroker is not null)
            {
                try
                {
                    var connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
                    foreach (var conn in connections)
                    {
                        try
                        {
                            var roots = await conn.Capability.Roots(ct).ConfigureAwait(false);
                            foreach (var root in roots)
                            {
                                if (root.Kind == ItemKind.Folder)
                                {
                                    try
                                    {
                                        var children = await conn.Capability.List(root.Id, new(PageSize: 100), ct).ConfigureAwait(false);
                                        foreach (var child in children.Items)
                                        {
                                            AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, child));
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException)
                                    {
                                        LogRecoverable(ex);
                                    }
                                }
                                else
                                {
                                    AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, root));
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            LogRecoverable(ex);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRecoverable(ex);
                }
            }

            return true;
        }).ConfigureAwait(false);

        // 5. Merge overlay entries
        var overlay = GetBrainOverlay();
        foreach (var (entry, _) in overlay.Values)
        {
            AddOrPreferNewest(byName, entry);
        }

        IEnumerable<WorkspaceFileEntry> results = byName.Values;
        if (trimmedQuery is not null)
        {
            results = results.Where(entry =>
                entry.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.VfsPath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.NeuronUri.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase));
        }

        return results
            .OrderByDescending(e => e.Modified ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<WorkspaceFileEntry?> ResolveFileAsync(string? nameOrUriOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath))
        {
            return null;
        }

        var cleanName = ExtractCleanFileName(nameOrUriOrPath);
        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);

        var overlay = GetBrainOverlay();
        if (!string.IsNullOrWhiteSpace(cleanName) && overlay.TryGetValue(cleanName, out var exactOverlay))
        {
            return exactOverlay.Entry;
        }
        if (overlay.TryGetValue(canonicalPath, out var byPathOverlay))
        {
            return byPathOverlay.Entry;
        }

        var cleanSlug = SlugSegment(cleanName);
        foreach (var (entry, _) in overlay.Values)
        {
            if (string.Equals(entry.Name, cleanName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.VfsPath, canonicalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.VfsPath, nameOrUriOrPath.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.NeuronUri, nameOrUriOrPath.Trim(), StringComparison.OrdinalIgnoreCase)
                || (cleanSlug.Length > 0 && string.Equals(SlugSegment(entry.Name), cleanSlug, StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
        }

        // Try statting through StorageVfs
        var vfsEntry = await WithCallerAsync<WorkspaceFileEntry?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            try
            {
                var item = await vfs.Stat(canonicalPath, ct).ConfigureAwait(false);
                if (item is not null && item.Kind != ItemKind.Folder)
                {
                    var mount = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "work";
                    return MapVfsItem(mount, item);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            try
            {
                var item = await vfs.Stat($"/work/{cleanName}", ct).ConfigureAwait(false);
                if (item is not null && item.Kind != ItemKind.Folder)
                {
                    return MapVfsItem("work", item);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            return null;
        }).ConfigureAwait(false);

        if (vfsEntry is not null)
        {
            return vfsEntry;
        }

        var searched = await ListFilesAsync(cleanName, ct).ConfigureAwait(false);
        var match = FindBestMatch(searched, cleanName, cleanSlug, nameOrUriOrPath);
        if (match is not null)
        {
            return match;
        }

        var allFiles = await ListFilesAsync(null, ct).ConfigureAwait(false);
        return FindBestMatch(allFiles, cleanName, cleanSlug, nameOrUriOrPath);
    }

    public async Task<string?> ReadTextAsync(string nameOrUriOrPath, CancellationToken ct = default)
    {
        var bytes = await ReadBytesAsync(nameOrUriOrPath, ct).ConfigureAwait(false);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public async Task<byte[]?> ReadBytesAsync(string nameOrUriOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath))
        {
            return null;
        }

        var cleanName = ExtractCleanFileName(nameOrUriOrPath);
        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);
        var overlay = GetBrainOverlay();

        if (!string.IsNullOrWhiteSpace(cleanName) && overlay.TryGetValue(cleanName, out var direct))
        {
            return direct.Bytes;
        }
        if (overlay.TryGetValue(canonicalPath, out var byPath))
        {
            return byPath.Bytes;
        }

        // Try reading through StorageVfs
        var vfsBytes = await WithCallerAsync<byte[]?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);

            try
            {
                var content = await vfs.Read(canonicalPath, new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                return content.Bytes.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            try
            {
                var content = await vfs.Read($"/work/{cleanName}", new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                return content.Bytes.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            try
            {
                var content = await vfs.Read($"/local/{cleanName}", new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                return content.Bytes.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            try
            {
                var content = await vfs.Read($"/drive/{cleanName}", new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                return content.Bytes.ToArray();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            return null;
        }).ConfigureAwait(false);

        if (vfsBytes is not null)
        {
            return vfsBytes;
        }

        // Fallback: resolve item through connections
        var resolved = await ResolveFileAsync(nameOrUriOrPath, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        if (overlay.TryGetValue(resolved.Name, out var byResolvedName))
        {
            return byResolvedName.Bytes;
        }

        return await WithCallerAsync<byte[]?>(async effectiveBroker =>
        {
            if (effectiveBroker is null)
            {
                return null;
            }

            IReadOnlyList<CapabilityConnection<IStorage>> connections = [];
            try
            {
                connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
                return null;
            }

            var targetConn = connections.FirstOrDefault(c => string.Equals(c.ConnectionKey, resolved.Connection, StringComparison.OrdinalIgnoreCase))
                ?? connections.FirstOrDefault();
            if (targetConn is null)
            {
                return null;
            }

            try
            {
                var content = await targetConn.Capability.Read(new ItemId(resolved.ItemId), new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false);
                return content.Bytes.ToArray();
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
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            cleanName = "artifact.bin";
        }

        var bytes = content.ToArray();
        var now = DateTimeOffset.UtcNow;
        var mountPoint = canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "work";
        var connectionKey = mountPoint == "work" ? "local-disk" : mountPoint;
        var neuronUri = BuildNeuronUri(BrainId, connectionKey, cleanName);

        var entry = new WorkspaceFileEntry(
            Name: cleanName,
            Connection: connectionKey,
            ItemId: canonicalPath,
            Kind: "file",
            Size: bytes.LongLength,
            NeuronUri: neuronUri,
            VfsPath: canonicalPath,
            Modified: now);

        // 1. Write through StorageVfs
        await WithCallerAsync(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            try
            {
                await vfs.Write(canonicalPath, bytes, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }

            // Also persist directly into any writable connection root
            if (effectiveBroker is not null)
            {
                try
                {
                    var connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
                    foreach (var conn in connections)
                    {
                        try
                        {
                            var roots = await conn.Capability.Roots(ct).ConfigureAwait(false);
                            var writableRoot = roots.FirstOrDefault(r => r.Flags.HasFlag(ItemFlags.Write));
                            if (writableRoot is not null)
                            {
                                await conn.Capability.Write(WritePlan.WriteFile(writableRoot.Id, cleanName, bytes, ConflictPolicy.Overwrite), ct).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            LogRecoverable(ex);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRecoverable(ex);
                }
            }

            return true;
        }).ConfigureAwait(false);

        // 2. Cache in overlay
        var overlay = GetBrainOverlay();
        overlay[cleanName] = (entry, bytes);
        overlay[canonicalPath] = (entry, bytes);

        // 3. Immediately refresh / notify FilesWorkspaceNeuron
        if (_services is not null)
        {
            try
            {
                var workspace = SiloNeurons.Get<IFilesWorkspace>(_services, IFilesWorkspace.Key(BrainId));
                await workspace.Open(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        return entry;
    }

    public Task<WorkspaceFileEntry> WriteTextAsync(
        string fileNameOrPath,
        string content,
        CancellationToken ct = default)
        => WriteFileAsync(fileNameOrPath, Encoding.UTF8.GetBytes(content ?? string.Empty), ct);

    public async Task<bool> DeleteFileAsync(string fileNameOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var canonicalPath = ToCanonicalVfsPath(fileNameOrPath);
        var cleanName = ExtractCleanFileName(canonicalPath);

        var overlay = GetBrainOverlay();
        overlay.TryRemove(cleanName, out _);
        overlay.TryRemove(canonicalPath, out _);

        try
        {
            var fallback = GetFallbackWorkspaceStorage(BrainId);
            await fallback.Write(WritePlan.Delete(new ItemId(cleanName)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecoverable(ex);
        }

        if (_services is not null)
        {
            try
            {
                var workspace = SiloNeurons.Get<IFilesWorkspace>(_services, IFilesWorkspace.Key(BrainId));
                await workspace.Open(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        return true;
    }

    private ConcurrentDictionary<string, (WorkspaceFileEntry Entry, byte[] Bytes)> GetBrainOverlay() => _overlay;

    private async Task<T> WithCallerAsync<T>(Func<ICapabilityBroker?, Task<T>> action)
    {
        var resolvedBroker = _broker ?? _services?.GetService<ICapabilityBroker>();
        var filesAppKey = IApp.Key(BrainId, "files");
        var effectiveBroker = resolvedBroker is CapabilityBroker cb
            ? cb.ForApp(filesAppKey)
            : resolvedBroker;

        var hadPrevious = RuntimeCallerContext.TryGet(out var previous);
        IDisposable? stampScope = null;
        if (_services is not null)
        {
            try
            {
                var baseCaller = hadPrevious && previous is not null && previous.BrainId == BrainId
                    ? previous
                    : await HostCaller.ForBrain(_services, BrainId).ConfigureAwait(false);
                if (baseCaller is not null)
                {
                    stampScope = RuntimeCallerContext.StampScope(HostCaller.AsApp(baseCaller, filesAppKey));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LogRecoverable(error);
            }
        }

        try
        {
            return await action(effectiveBroker).ConfigureAwait(false);
        }
        finally
        {
            stampScope?.Dispose();
            if (hadPrevious && previous is not null)
            {
                RuntimeCallerContext.Stamp(previous);
            }
            else
            {
                RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
            }
        }
    }

    private WorkspaceFileEntry MapVfsItem(string connection, VfsItem item)
    {
        var fileName = item.Name;
        var connKey = string.IsNullOrWhiteSpace(connection) || string.Equals(connection, "work", StringComparison.OrdinalIgnoreCase)
            ? "local-disk"
            : connection;
        var neuronUri = BuildNeuronUri(BrainId, connKey, fileName);
        var kind = item.Kind == ItemKind.Folder ? "folder" : "file";

        return new WorkspaceFileEntry(
            Name: fileName,
            Connection: connKey,
            ItemId: item.Path,
            Kind: kind,
            Size: item.Size,
            NeuronUri: neuronUri,
            VfsPath: item.Path,
            Modified: item.Modified);
    }

    private WorkspaceFileEntry MapItem(string connection, Item item)
    {
        var rawName = string.IsNullOrWhiteSpace(item.Name) ? item.Id.Value : item.Name.Trim();
        var fileName = Path.GetFileName(rawName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = rawName;
        }

        var connKey = string.IsNullOrWhiteSpace(connection) ? "local-disk" : connection;
        var neuronUri = BuildNeuronUri(BrainId, connKey, fileName);
        var vfsPath = rawName.StartsWith('/') ? rawName : $"/work/{fileName}";
        var kind = item.Kind == ItemKind.Folder ? "folder" : item.Kind == ItemKind.Link ? "link" : "file";

        return new WorkspaceFileEntry(
            Name: fileName,
            Connection: connKey,
            ItemId: item.Id.Value,
            Kind: kind,
            Size: item.Size,
            NeuronUri: neuronUri,
            VfsPath: vfsPath,
            Modified: item.Modified);
    }

    private static void AddOrPreferNewest(Dictionary<string, WorkspaceFileEntry> byName, WorkspaceFileEntry candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Name))
        {
            return;
        }

        if (!byName.TryGetValue(candidate.Name, out var existing))
        {
            byName[candidate.Name] = candidate;
            return;
        }

        var candidateTime = candidate.Modified ?? DateTimeOffset.MinValue;
        var existingTime = existing.Modified ?? DateTimeOffset.MinValue;
        if (candidateTime >= existingTime)
        {
            byName[candidate.Name] = candidate;
        }
    }

    private static WorkspaceFileEntry? FindBestMatch(
        IReadOnlyList<WorkspaceFileEntry> candidates,
        string cleanName,
        string cleanSlug,
        string rawQuery)
    {
        foreach (var entry in candidates)
        {
            if (string.Equals(entry.Name, cleanName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.VfsPath, rawQuery.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.NeuronUri, rawQuery.Trim(), StringComparison.OrdinalIgnoreCase)
                || (cleanSlug.Length > 0 && string.Equals(SlugSegment(entry.Name), cleanSlug, StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
        }

        return candidates.FirstOrDefault(e => e.Kind != "folder" && e.Name.Contains(cleanName, StringComparison.OrdinalIgnoreCase));
    }

    public static string ExtractCleanFileName(string? nameOrUriOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath))
        {
            return string.Empty;
        }

        var trimmed = nameOrUriOrPath.Trim().Trim('"', '\'', '`', '(', ')', '[', ']', ';', ',');
        if (trimmed.StartsWith('@'))
        {
            trimmed = trimmed[1..].Trim();
        }

        var neuronIdx = trimmed.IndexOf("neuron://", StringComparison.OrdinalIgnoreCase);
        if (neuronIdx >= 0)
        {
            var endIdx = trimmed.IndexOfAny([' ', ')', '\r', '\n', ']'], neuronIdx);
            var uriSlice = endIdx > neuronIdx ? trimmed[neuronIdx..endIdx] : trimmed[neuronIdx..];
            var lastSlash = uriSlice.LastIndexOf('/');
            return lastSlash >= 0 && lastSlash + 1 < uriSlice.Length ? uriSlice[(lastSlash + 1)..] : uriSlice;
        }

        var normalized = trimmed.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName) ? normalized.Trim('/') : fileName;
    }

    internal static string BuildNeuronUri(string brainId, string connection, string name)
    {
        var brainSlug = string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim();
        var connSlug = SlugSegment(string.IsNullOrWhiteSpace(connection) ? "local-disk" : connection);
        var itemSlug = SlugSegment(name);
        return $"neuron://{brainSlug}/{connSlug}.files/{itemSlug}";
    }

    internal static string SlugSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "file";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch is '/' or '\\')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length > 0 ? slug : "file";
    }

    private static void LogRecoverable(Exception error)
    {
        _ = error.Message;
    }
}
