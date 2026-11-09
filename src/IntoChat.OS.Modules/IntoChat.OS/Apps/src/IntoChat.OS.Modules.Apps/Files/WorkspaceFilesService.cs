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
    private static readonly string[] CanonicalMounts = ["work", "local", "drive"];

    private readonly ConcurrentDictionary<string, (WorkspaceFileEntry Entry, byte[] Bytes)> _overlay = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider? _services;
    private readonly ICapabilityBroker? _broker;

    public WorkspaceFilesService(IServiceProvider? services = null, string brainId = "local", ICapabilityBroker? broker = null)
    {
        _services = services;
        _broker = broker;
        BrainId = NormalizeBrainId(brainId);
    }

    public string BrainId { get; }
    public const long MaxReadBytes = 16 * 1024 * 1024;

    public static WorkspaceFilesService ForBrain(IServiceProvider? services = null, string brainId = "local", ICapabilityBroker? broker = null)
        => new(services, brainId, broker);

    public static LocalCloudNeuron GetFallbackWorkspaceStorage(string brainId)
        => _sharedFallbackWorkStorages.GetOrAdd(NormalizeBrainId(brainId), static _ => new LocalCloudNeuron());

    public static LocalCloudNeuron GetFallbackLocalStorage(string brainId)
        => _sharedFallbackLocalStorages.GetOrAdd(NormalizeBrainId(brainId), static _ =>
        {
            try
            {
                var cwd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)) { return new LocalCloudNeuron(cwd); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }
            return new LocalCloudNeuron();
        });

    public static LocalCloudNeuron GetFallbackDriveStorage(string brainId)
        => _sharedFallbackDriveStorages.GetOrAdd(NormalizeBrainId(brainId), static _ => new LocalCloudNeuron());

    public async Task<IStorageVfs> BuildVfsAsync(ICapabilityBroker? effectiveBroker, CancellationToken ct = default)
    {
        IReadOnlyList<CapabilityConnection<IStorage>> connections = [];
        if (effectiveBroker is not null)
        {
            try { connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        var workStorage = ResolveMountStorage(connections, ["cloud", "work"], LocalCloudNeuron.TypeName, fallbackFirstConnection: true)
            ?? GetFallbackWorkspaceStorage(BrainId);
        var localStorage = ResolveMountStorage(connections, ["local-disk", "disk"], "local-disk", fallbackFirstConnection: false)
            ?? GetFallbackLocalStorage(BrainId);
        var driveStorage = ResolveMountStorage(connections, ["drive", "blob", "google"], neuronType: null, fallbackFirstConnection: false)
            ?? GetFallbackDriveStorage(BrainId);

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

    public static string ToCanonicalVfsPath(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) { return "/work/artifact.bin"; }
        var normalized = fileNameOrPath.Trim().Trim('"', '\'', '`').Replace('\\', '/');
        if (normalized.StartsWith('/'))
        {
            var segments = normalized.TrimStart('/').Split('/', 2);
            var mount = segments[0].ToLowerInvariant();
            return mount is "work" or "local" or "drive"
                ? $"/{mount}/{(segments.Length > 1 ? segments[1] : string.Empty)}".TrimEnd('/')
                : $"/work/{normalized.TrimStart('/')}";
        }
        return $"/work/{ExtractCleanFileName(normalized)}";
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

        foreach (var (entry, _) in _overlay.Values) { AddOrPreferNewest(byName, entry); }

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

        var cleanName = ExtractCleanFileName(nameOrUriOrPath);
        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);

        if (!string.IsNullOrWhiteSpace(cleanName) && _overlay.TryGetValue(cleanName, out var exactOverlay)) { return exactOverlay.Entry; }
        if (_overlay.TryGetValue(canonicalPath, out var byPathOverlay)) { return byPathOverlay.Entry; }

        var cleanSlug = SlugSegment(cleanName);
        foreach (var (entry, _) in _overlay.Values)
        {
            if (IsMatchingEntry(entry, cleanName, cleanSlug, canonicalPath, nameOrUriOrPath)) { return entry; }
        }

        var vfsEntry = await WithCallerAsync<WorkspaceFileEntry?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            foreach (var candidatePath in new[] { canonicalPath, $"/work/{cleanName}" })
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

        var cleanName = ExtractCleanFileName(nameOrUriOrPath);
        var canonicalPath = ToCanonicalVfsPath(nameOrUriOrPath);

        if (!string.IsNullOrWhiteSpace(cleanName) && _overlay.TryGetValue(cleanName, out var direct)) { return direct.Bytes; }
        if (_overlay.TryGetValue(canonicalPath, out var byPath)) { return byPath.Bytes; }

        var vfsBytes = await WithCallerAsync<byte[]?>(async effectiveBroker =>
        {
            var vfs = await BuildVfsAsync(effectiveBroker, ct).ConfigureAwait(false);
            foreach (var candidatePath in new[] { canonicalPath, $"/work/{cleanName}", $"/local/{cleanName}", $"/drive/{cleanName}" })
            {
                try { return (await vfs.Read(candidatePath, new ByteRange(0, MaxReadBytes), ct).ConfigureAwait(false)).Bytes.ToArray(); }
                catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
            }
            return null;
        }).ConfigureAwait(false);

        if (vfsBytes is not null) { return vfsBytes; }

        var resolved = await ResolveFileAsync(nameOrUriOrPath, ct).ConfigureAwait(false);
        if (resolved is null) { return null; }
        if (_overlay.TryGetValue(resolved.Name, out var byResolvedName)) { return byResolvedName.Bytes; }

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

            if (effectiveBroker is not null)
            {
                try
                {
                    foreach (var conn in await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false))
                    {
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

        _overlay[cleanName] = (entry, bytes);
        _overlay[canonicalPath] = (entry, bytes);
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

        _overlay.TryRemove(cleanName, out _);
        _overlay.TryRemove(canonicalPath, out _);

        try { await GetFallbackWorkspaceStorage(BrainId).Write(WritePlan.Delete(new ItemId(cleanName)), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogRecoverable(ex); }

        await TryRefreshFilesWorkspaceAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task TryRefreshFilesWorkspaceAsync(CancellationToken ct)
    {
        if (_services is null) { return; }
        try { await SiloNeurons.Get<IFilesWorkspace>(_services, IFilesWorkspace.Key(BrainId)).Open(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
    }

    private async Task<T> WithCallerAsync<T>(Func<ICapabilityBroker?, Task<T>> action)
    {
        var resolvedBroker = _broker ?? _services?.GetService<ICapabilityBroker>();
        var filesAppKey = IApp.Key(BrainId, "files");
        var effectiveBroker = resolvedBroker is CapabilityBroker cb ? cb.ForApp(filesAppKey) : resolvedBroker;

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
            catch (Exception error) when (error is not OperationCanceledException) { LogRecoverable(error); }
        }

        try
        {
            return await action(effectiveBroker).ConfigureAwait(false);
        }
        finally
        {
            stampScope?.Dispose();
            if (hadPrevious && previous is not null) { RuntimeCallerContext.Stamp(previous); }
            else { RequestContext.Remove(RuntimeCallerContext.RequestContextKey); }
        }
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
            return lastSlash >= 0 && lastSlash + 1 < uriSlice.Length ? uriSlice[(lastSlash + 1)..] : uriSlice;
        }

        var normalized = trimmed.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        return string.IsNullOrWhiteSpace(fileName) ? normalized.Trim('/') : fileName;
    }

    internal static string BuildNeuronUri(string brainId, string connection, string name)
        => $"neuron://{NormalizeBrainId(brainId)}/{SlugSegment(string.IsNullOrWhiteSpace(connection) ? "local-disk" : connection)}.files/{SlugSegment(name)}";

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
        => string.IsNullOrWhiteSpace(brainId) ? "local" : brainId.Trim();

    private static void LogRecoverable(Exception error) => _ = error.Message;
}
