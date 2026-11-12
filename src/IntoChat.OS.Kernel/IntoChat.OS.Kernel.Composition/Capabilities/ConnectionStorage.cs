using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Kernel.Composition.Capabilities;

// The broker binds item IDs to this exact connection. Streams remain host-local: reads use bounded ranges and writes use
// expiring upload sessions on the provider. Provider IDs are never guessed from a path or tested against another connection.
public sealed class ConnectionStorage(
    IStorage remote,
    string? connectionKey = null,
    IStorageUpload? uploads = null,
    IStorageWriteAuthorizer? authorizer = null,
    string? sandboxBadge = null) : IStorage
{
    private readonly IStorage _remote = remote ?? throw new ArgumentNullException(nameof(remote));
    private readonly string? _prefix = connectionKey is null ? null : Root(connectionKey);

    public string? SandboxBadge => sandboxBadge;

    public static string Root(string connectionKey) => "storage:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(connectionKey)) + ":";

    private ItemId Bound(ItemId id) => _prefix is null ? id : new(_prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(id.Value)));
    private ItemId Local(ItemId id)
    {
        if (_prefix is null) { return id; }
        if (id.Value is null || !id.Value.StartsWith(_prefix, StringComparison.Ordinal)) { throw new Denied(id); }
        try { return new(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(id.Value[_prefix.Length..]))); }
        catch (FormatException error) { throw new Denied(id, error); }
    }

    private Item Bound(Item item) => item with { Id = Bound(item.Id), Target = item.Target is { } target ? Bound(target) : null };
    private Page<Item> Bound(Page<Item> page) => page with { Items = page.Items.Select(Bound).ToArray() };
    private WritePlan Local(WritePlan plan) => plan with
    {
        Source = plan.Source is { } source ? Local(source) : null,
        TargetFolder = plan.TargetFolder is { } target ? Local(target) : null,
    };

    public async Task<StorageDescriptor> Describe(CancellationToken ct = default)
    {
        var descriptor = await Typed(() => _remote.Describe(ct)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(sandboxBadge) && !descriptor.Label.Contains(sandboxBadge, StringComparison.Ordinal))
        {
            return descriptor with { Label = $"{descriptor.Label} [{sandboxBadge}]" };
        }
        return descriptor;
    }

    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => (await Typed(() => _remote.Roots(ct)).ConfigureAwait(false)).Select(Bound).ToArray();

    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => Bound(await Typed(() => _remote.List(Local(folder), query, ct)).ConfigureAwait(false));

    public async Task<Item> Stat(ItemId id, CancellationToken ct = default) => Bound(await Typed(() => _remote.Stat(Local(id), ct)).ConfigureAwait(false));

    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => Typed(() => _remote.Read(Local(id), range, ct));

    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        var primitive = Local(plan);
        return Authorized(primitive, () => Typed(() => _remote.Write(primitive, ct)), ct);
    }

    public async Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => Bound(await Typed(() => _remote.Search(query with { Within = query.Within is { } within ? Local(within) : null }, ct)).ConfigureAwait(false));

    public async Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => (await Typed(() => _remote.Ancestors(Local(id), ct)).ConfigureAwait(false)).Select(Bound).ToArray();

    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var stat = await Stat(item, ct).ConfigureAwait(false);
        if (stat.Kind == ItemKind.Folder) { throw new Unsupported(item); }
        var offset = range?.Offset ?? 0;
        var length = stat.Size is { } size ? Math.Max(0, size - offset) : (long?)null;
        if (range?.Length is { } requested) { length = length is { } remaining ? Math.Min(remaining, requested) : requested; }
        return new StorageReadStream(new RemoteReadStream(this, item, offset, length, ct), length, "application/octet-stream", stat.Hash, canSeek: false, stat.Modified);
    }

    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var transport = uploads ?? _remote as IStorageUpload ?? throw new Unsupported(plan.TargetFolder);
        var primitive = Local(plan);
        var id = await Authorized(primitive, () => Typed(() => transport.BeginUpload(primitive, ct)), ct).ConfigureAwait(false);
        return new RemoteWriteStream(this, transport, id, Bound);
    }

    private Task<T> Authorized<T>(WritePlan plan, Func<Task<T>> send, CancellationToken ct)
        => authorizer is not null && connectionKey is not null ? authorizer.Invoke(connectionKey, plan, send, ct) : send();

    private async Task<T> Typed<T>(Func<Task<T>> call)
    {
        var hadCaller = RuntimeCallerContext.TryGet(out var current);
        IDisposable? stampScope = null;
        if (connectionKey is { Length: > 0 } && (!hadCaller || current is null || current.Kind == CallerKind.App))
        {
            var slash = connectionKey.IndexOf('/', StringComparison.Ordinal);
            var owner = slash > 0 ? connectionKey[..slash] : connectionKey;
            var brain = current?.BrainId is { Length: > 0 } b ? b : owner;
            // The connection's owner here is a brain id, nobody's account: the kernel reads the connection as its platform.
            stampScope = RuntimeCallerContext.StampScope(CapabilityCallers.Platform(owner, owner, brain));
        }
        try { return await call().ConfigureAwait(false); }
        catch (Exception error) when (error is not StorageException && StorageErrors.FromMessage(error.Message) is { } typed)
        { throw typed; }
        finally
        {
            stampScope?.Dispose();
        }
    }

    private sealed class RemoteReadStream(ConnectionStorage storage, ItemId item, long offset, long? length, CancellationToken opened) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length ?? throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            opened.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || length is { } end && _position >= end) { return 0; }
            var count = Math.Min(buffer.Length, StorageUploadSessions.ChunkSize);
            if (length is { } remaining) { count = (int)Math.Min(count, remaining - _position); }
            var content = await storage.Read(item, new ByteRange(offset + _position, count), cancellationToken).ConfigureAwait(false);
            if (content.Bytes.Length > count) { throw new IOException("Storage returned more than the requested range."); }
            content.Bytes.CopyTo(buffer);
            _position += content.Bytes.Length;
            if (content.Bytes.Length < count) { length = _position; }
            return content.Bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RemoteWriteStream : StorageWriteStream
    {
        private readonly ConnectionStorage _storage;
        private readonly IStorageUpload _transport;
        private readonly string _id;
        private readonly Func<ItemId, ItemId> _bind;
        public RemoteWriteStream(ConnectionStorage storage, IStorageUpload transport, string id, Func<ItemId, ItemId> bind) : base(new UploadStream(storage, transport, id))
        { _storage = storage; _transport = transport; _id = id; _bind = bind; }
        protected override async Task<ItemId> CommitCore(CancellationToken ct) => _bind(await _storage.Typed(() => _transport.CommitUpload(_id, Content.Position, ct)).ConfigureAwait(false));
        protected override Task AbortCore(CancellationToken ct) => _storage.Typed(async () => { await _transport.AbortUpload(_id, ct).ConfigureAwait(false); return true; });
    }

    private sealed class UploadStream(ConnectionStorage storage, IStorageUpload transport, string id) : Stream
    {
        private long _position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                var count = Math.Min(buffer.Length, StorageUploadSessions.ChunkSize);
                var chunk = buffer[..count].ToArray();
                await storage.Typed(async () => { await transport.AppendUpload(id, _position, chunk, cancellationToken).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                _position += count;
                buffer = buffer[count..];
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
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
