using CultureInfo = System.Globalization.CultureInfo;
using NumberStyles = System.Globalization.NumberStyles;
using System.Text;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Authority.Connections;

// storage@1 over a connection neuron that implements only the 0.6 ILegacyStorage (D-03): what the capability broker binds for
// such a connection until Files moves in Phase 7, when this adapter and ILegacyStorage are deleted. Read-only: the descriptor
// offers Read only, quota is Unknown (never 0), ids are the legacy locations and pages are cut client-side (cursor = offset).
public sealed class LegacyStorageAdapter(ILegacyStorage legacy, string label = "Storage", string kind = "legacy") : IStorage
{
    private const string TextMediaType = "text/plain; charset=utf-8";

    private readonly ILegacyStorage _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));

    public Task<StorageDescriptor> Describe(CancellationToken ct = default)
        => Task.FromResult(new StorageDescriptor(label, kind, Quota.Unknown, ItemFlags.Read));

    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
        => [.. (await _legacy.Roots(ct).ConfigureAwait(false)).Select(ToItem)];

    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = (await _legacy.List(folder.Value ?? string.Empty, ct).ConfigureAwait(false)).Select(ToItem);
        IEnumerable<Item> sorted = query.Sort == SortKey.FolderThenName
            ? entries.OrderBy(item => item.Kind == ItemKind.Folder ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal)
            : entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.Ordinal);
        var all = (query.Descending ? sorted.Reverse() : sorted).ToList();
        return Cut(all, query.Cursor, query.PageSize);
    }

    // The legacy contract has no stat: the last breadcrumb of a location is the location itself.
    public async Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        var chain = await _legacy.Ancestors(id.Value ?? string.Empty, ct).ConfigureAwait(false);
        return chain.Length > 0 ? ToItem(chain[^1]) : throw new NotFound(id);
    }

    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(await _legacy.Read(id.Value ?? string.Empty, ct).ConfigureAwait(false));
        return new Content(Slice(bytes, range), TextMediaType, bytes.Length);
    }

    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => throw new Unsupported(null);

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => throw new Unsupported(null);

    // storage@1 ancestors exclude the item itself; the legacy chain includes it.
    public async Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
    {
        var chain = await _legacy.Ancestors(id.Value ?? string.Empty, ct).ConfigureAwait(false);
        return [.. chain.Take(Math.Max(0, chain.Length - 1)).Select(ToItem)];
    }

    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var content = await Read(item, range, ct).ConfigureAwait(false);
        return new StorageReadStream(new MemoryStream(content.Bytes.ToArray(), writable: false), content.Bytes.Length, content.MediaType, null, canSeek: true, null);
    }

    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => throw new Unsupported(null);

    private static Item ToItem(StorageEntry entry)
        => new(new ItemId(entry.Location), entry.Name, entry.IsFolder ? ItemKind.Folder : ItemKind.File, null, null, ItemFlags.Read);

    internal static Page<Item> Cut(IReadOnlyList<Item> all, string? cursor, int pageSize)
    {
        var offset = cursor is { Length: > 0 } && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0, all.Count) : 0;
        var end = Math.Min(all.Count, offset + pageSize);
        return new Page<Item>([.. all.Skip(offset).Take(end - offset)], end < all.Count ? end.ToString(CultureInfo.InvariantCulture) : null);
    }

    private static ReadOnlyMemory<byte> Slice(byte[] bytes, ByteRange? range)
    {
        if (range is not { } r) { return bytes; }
        var offset = (int)Math.Min(r.Offset, bytes.Length);
        var length = r.Length is { } l ? (int)Math.Min(l, bytes.Length - offset) : bytes.Length - offset;
        return bytes.AsMemory(offset, length);
    }
}
