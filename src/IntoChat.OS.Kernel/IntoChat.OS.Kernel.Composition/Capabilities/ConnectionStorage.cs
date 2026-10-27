using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// storage@1 of a connection neuron that implements it, as the broker hands it to a caller (05-07/05-08). Every call travels to
// the neuron; the stream handles stay host-local (they never cross a grain boundary): OpenRead is served from Read, OpenWrite is
// refused until Phase 7 wires writes. A typed storage error crosses the boundary as its message ("storage {code}: {id}") and is
// raised here again as its type.
public sealed class ConnectionStorage(IStorage remote) : IStorage
{
    private readonly IStorage _remote = remote ?? throw new ArgumentNullException(nameof(remote));

    public Task<StorageDescriptor> Describe(CancellationToken ct = default) => Typed(() => _remote.Describe(ct));

    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => Typed(() => _remote.Roots(ct));

    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => Typed(() => _remote.List(folder, query, ct));

    public Task<Item> Stat(ItemId id, CancellationToken ct = default) => Typed(() => _remote.Stat(id, ct));

    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => Typed(() => _remote.Read(id, range, ct));

    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => Typed(() => _remote.Write(plan, ct));

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => Typed(() => _remote.Search(query, ct));

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => Typed(() => _remote.Ancestors(id, ct));

    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var stat = await Stat(item, ct).ConfigureAwait(false);
        if (stat.Kind == ItemKind.Folder) { throw new Unsupported(item); }
        var content = await Read(item, range, ct).ConfigureAwait(false);
        var bytes = content.Bytes.ToArray();
        return new StorageReadStream(new MemoryStream(bytes, writable: false), bytes.Length, content.MediaType, stat.Hash, canSeek: true, stat.Modified);
    }

    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = await Describe(ct).ConfigureAwait(false);
        throw new Unsupported(null);
    }

    private static async Task<T> Typed<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception error) when (error is not StorageException && StorageErrors.FromMessage(error.Message) is { } typed)
        { throw typed; }
    }
}

// The storage error a message names, when it is one ("storage not-found: id", "storage unsupported").
public static class StorageErrors
{
    private const string Prefix = "storage ";

    public static StorageException? FromMessage(string? message)
    {
        if (message is null || !message.StartsWith(Prefix, StringComparison.Ordinal)) { return null; }
        var rest = message[Prefix.Length..];
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        var code = colon < 0 ? rest.Trim() : rest[..colon];
        ItemId? item = colon < 0 ? null : new ItemId(rest[(colon + 2)..]);
        return code switch
        {
            "not-found" => new NotFound(item),
            "conflict" => new Conflict(item),
            "denied" => new Denied(item),
            "quota-exceeded" => new QuotaExceeded(item),
            "unavailable" => new Unavailable(item),
            "needs-auth" => new NeedsAuth(item),
            "unsupported" => new Unsupported(item),
            _ => null,
        };
    }
}
