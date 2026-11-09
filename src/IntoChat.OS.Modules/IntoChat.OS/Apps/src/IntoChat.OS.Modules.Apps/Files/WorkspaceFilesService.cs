using System.Collections.Concurrent;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
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
}

public sealed class WorkspaceFilesService : IFilesVfsService
{
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

    public static WorkspaceFilesService ForBrain(
        IServiceProvider? services = null,
        string brainId = "local",
        ICapabilityBroker? broker = null) => new(services, brainId, broker);

    public async Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string? query = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var trimmedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var byName = new Dictionary<string, WorkspaceFileEntry>(StringComparer.OrdinalIgnoreCase);

        await WithCallerAsync(async effectiveBroker =>
        {
            if (effectiveBroker is not null)
            {
                IReadOnlyList<CapabilityConnection<IStorage>> connections = [];
                try
                {
                    connections = await effectiveBroker.ResolveAll<IStorage>(new BrainId(BrainId), ct).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    LogRecoverable(error);
                }

                foreach (var conn in connections)
                {
                    try
                    {
                        if (trimmedQuery is not null)
                        {
                            var page = await conn.Capability.Search(new(trimmedQuery, PageSize: 50), ct).ConfigureAwait(false);
                            foreach (var item in page.Items)
                            {
                                AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, item));
                            }
                        }
                        else
                        {
                            var roots = await conn.Capability.Roots(ct).ConfigureAwait(false);
                            foreach (var root in roots)
                            {
                                AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, root));
                                if (root.Kind == ItemKind.Folder)
                                {
                                    try
                                    {
                                        var children = await conn.Capability.List(root.Id, new(PageSize: 50), ct).ConfigureAwait(false);
                                        foreach (var child in children.Items)
                                        {
                                            AddOrPreferNewest(byName, MapItem(conn.ConnectionKey, child));
                                        }
                                    }
                                    catch (Exception error) when (error is not OperationCanceledException)
                                    {
                                        LogRecoverable(error);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        LogRecoverable(error);
                    }
                }
            }

            return true;
        }).ConfigureAwait(false);

        var overlay = GetBrainOverlay();
        foreach (var (entry, _) in overlay.Values)
        {
            if (trimmedQuery is null
                || entry.Name.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.VfsPath.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                || entry.NeuronUri.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase))
            {
                AddOrPreferNewest(byName, entry);
            }
        }

        return byName.Values
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
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            return null;
        }

        var overlay = GetBrainOverlay();
        if (overlay.TryGetValue(cleanName, out var exactOverlay))
        {
            return exactOverlay.Entry;
        }

        var cleanSlug = SlugSegment(cleanName);
        foreach (var (entry, _) in overlay.Values)
        {
            if (string.Equals(entry.Name, cleanName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.VfsPath, nameOrUriOrPath.Trim(), StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.NeuronUri, nameOrUriOrPath.Trim(), StringComparison.OrdinalIgnoreCase)
                || (cleanSlug.Length > 0 && string.Equals(SlugSegment(entry.Name), cleanSlug, StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
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

    public const long MaxReadBytes = 16 * 1024 * 1024;

    public async Task<byte[]?> ReadBytesAsync(string nameOrUriOrPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameOrUriOrPath))
        {
            return null;
        }

        var cleanName = ExtractCleanFileName(nameOrUriOrPath);
        var overlay = GetBrainOverlay();
        if (!string.IsNullOrWhiteSpace(cleanName) && overlay.TryGetValue(cleanName, out var direct))
        {
            return direct.Bytes;
        }

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
        var cleanName = ExtractCleanFileName(fileNameOrPath);
        if (string.IsNullOrWhiteSpace(cleanName))
        {
            cleanName = "artifact.bin";
        }

        var bytes = content.ToArray();
        var now = DateTimeOffset.UtcNow;
        var connectionKey = "local-disk";
        var itemId = cleanName;
        var vfsPath = cleanName.StartsWith('/') ? cleanName : $"/work/{cleanName}";
        var neuronUri = BuildNeuronUri(BrainId, connectionKey, cleanName);

        var entry = new WorkspaceFileEntry(
            Name: cleanName,
            Connection: connectionKey,
            ItemId: itemId,
            Kind: "file",
            Size: bytes.LongLength,
            NeuronUri: neuronUri,
            VfsPath: vfsPath,
            Modified: now);

        var overlay = GetBrainOverlay();
        overlay[cleanName] = (entry, bytes);

        entry = await WithCallerAsync(async effectiveBroker =>
        {
            var currentEntry = entry;
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
                                var opId = await conn.Capability
                                    .Write(WritePlan.WriteFile(writableRoot.Id, cleanName, bytes, ConflictPolicy.Rename), ct)
                                    .ConfigureAwait(false);
                                var writtenItemId = string.IsNullOrWhiteSpace(opId.Value) ? cleanName : opId.Value;
                                try
                                {
                                    var listed = await conn.Capability.List(writableRoot.Id, new(PageSize: 50), ct).ConfigureAwait(false);
                                    if (listed.Items.FirstOrDefault(i => string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) is { } foundItem)
                                    {
                                        writtenItemId = foundItem.Id.Value;
                                    }
                                }
                                catch (Exception error) when (error is not OperationCanceledException)
                                {
                                    LogRecoverable(error);
                                }

                                currentEntry = currentEntry with
                                {
                                    Connection = conn.ConnectionKey,
                                    ItemId = writtenItemId,
                                    NeuronUri = BuildNeuronUri(BrainId, conn.ConnectionKey, cleanName),
                                };
                                overlay[cleanName] = (currentEntry, bytes);
                                break;
                            }
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            LogRecoverable(error);
                        }
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    LogRecoverable(error);
                }
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

            return currentEntry;
        }).ConfigureAwait(false);

        return entry;
    }

    public Task<WorkspaceFileEntry> WriteTextAsync(
        string fileNameOrPath,
        string content,
        CancellationToken ct = default)
        => WriteFileAsync(fileNameOrPath, Encoding.UTF8.GetBytes(content ?? string.Empty), ct);

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
