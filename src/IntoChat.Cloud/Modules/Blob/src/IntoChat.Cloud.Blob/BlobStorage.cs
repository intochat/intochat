using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Spi;

namespace IntoChat.Cloud.Blob;

// storage@1 over one blob container. Folders are "/" prefixes. BlobClient strips a trailing slash, so an empty folder is a hidden "{prefix}/.keep" marker.
// Access recording belongs to the runtime broker.
internal sealed class BlobStorage(BlobContainerClient container, IOperations? operations) : IStorage
{
    internal const string AzuriteConnection =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    // Azure.Storage.Blobs 12.30.1 defaults to service version 2026-10-06. Azurite 3.37 supports through 2026-06-06.
    internal static BlobClientOptions ClientOptions { get; } = new(BlobClientOptions.ServiceVersion.V2026_06_06);

    internal static BlobServiceClient Open(string connection) => new(connection, ClientOptions);

    private const string Label = "IntoChat Cloud";
    private const string Kind = "azure-blob";
    // BlobClient drops a trailing slash, so an empty folder is a hidden marker blob rather than "{prefix}/".
    private const string FolderMark = ".keep";
    private const int SearchDepth = 6;
    private const int SearchCap = 500;
    private const ItemFlags Offered = ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder
        | ItemFlags.Copy | ItemFlags.Move | ItemFlags.Rename | ItemFlags.Delete;

    public async Task<StorageDescriptor> Describe(CancellationToken ct = default)
    {
        await Ensure(ct).ConfigureAwait(false);
        return new StorageDescriptor(Label, Kind, Quota.Unknown, Offered);
    }

    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
    {
        await Ensure(ct).ConfigureAwait(false);
        return [Folder("", "Cloud")];
    }

    public async Task<DigitalBrain.Capabilities.Storage.Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = BlobItemId.Read(folder);
        if (!await HasChild(path, ct).ConfigureAwait(false)) { throw await IsFile(path, ct).ConfigureAwait(false) ? new Unsupported(folder) : new NotFound(folder); }
        var children = await Children(path, ct).ConfigureAwait(false);
        return Cut(Sorted(children, query.Sort, query.Descending), query.Cursor, query.PageSize);
    }

    public async Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        var path = BlobItemId.Read(id);
        if (path.Length == 0 || await HasChild(path, ct).ConfigureAwait(false)) { return Folder(path, path.Length == 0 ? "Cloud" : NameOf(path)); }
        if (await IsFile(path, ct).ConfigureAwait(false)) { return await FileItem(path, ct).ConfigureAwait(false); }
        throw new NotFound(id);
    }

    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        await using var stream = await OpenRead(id, range, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var props = await container.GetBlobClient(BlobItemId.Read(id)).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        return new Content(buffer.ToArray(), stream.ContentType, props.Value.ContentLength);
    }

    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();
        var host = operations ?? throw new InvalidOperationException("Blob storage writes need the host's operations.");
        var operation = await host.Start(new BlobOperation(plan with { Inline = null }, plan.Inline?.Length,
            plan.Inline is { } bytes ? Convert.ToHexStringLower(SHA256.HashData(bytes.Span)) : null), ct).ConfigureAwait(false);
        _ = Task.Run(() => Complete(plan, operation, ct), CancellationToken.None);
        return operation.Id;
    }

    public async Task<DigitalBrain.Capabilities.Storage.Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var within = query.Within is { } folder ? BlobItemId.Read(folder) : "";
        var found = new List<Item>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, within.Length == 0 ? null : within + "/", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (blob.Name.EndsWith('/') || blob.Name.Contains("/.partials/", StringComparison.Ordinal) || blob.Name.StartsWith(".partials/", StringComparison.Ordinal)) { continue; }
            var relative = blob.Name;
            if (relative.Split('/').Any(part => part is ".keep" or ".partials") || relative.EndsWith(".intochat-partial", StringComparison.Ordinal)) { continue; }
            if (relative.Count(ch => ch == '/') + 1 > SearchDepth) { continue; }
            if (!NameOf(relative).Contains(query.Text, StringComparison.OrdinalIgnoreCase)) { continue; }
            found.Add(await FileItem(relative, ct).ConfigureAwait(false));
            if (found.Count >= SearchCap) { break; }
        }
        return Cut(Sorted(found, DigitalBrain.Capabilities.Storage.SortKey.FolderThenName, descending: false), query.Cursor, query.PageSize);
    }

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
    {
        var path = BlobItemId.Read(id);
        if (path.Length == 0) { return Task.FromResult<IReadOnlyList<Item>>([]); }
        var chain = new List<Item> { Folder("", "Cloud") };
        var parts = path.Split('/');
        var walked = "";
        for (var index = 0; index < parts.Length - 1; index++)
        {
            walked = walked.Length == 0 ? parts[index] : walked + "/" + parts[index];
            chain.Add(Folder(walked, parts[index]));
        }
        return Task.FromResult<IReadOnlyList<Item>>(chain);
    }

    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var path = BlobItemId.Read(item);
        if (await HasChild(path, ct).ConfigureAwait(false) && !await IsFile(path, ct).ConfigureAwait(false)) { throw new Unsupported(item); }
        var client = container.GetBlobClient(path);
        if (!await client.ExistsAsync(ct).ConfigureAwait(false)) { throw new NotFound(item); }
        var props = await client.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        var length = props.Value.ContentLength;
        var offset = Math.Min(range?.Offset ?? 0, length);
        var count = range?.Length is { } wanted ? Math.Min(wanted, length - offset) : length - offset;
        var stream = await client.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false) { Position = offset }, ct).ConfigureAwait(false);
        Stream content = range is null ? stream : new Bounded(stream, count);
        var type = string.IsNullOrWhiteSpace(props.Value.ContentType) ? ContentType(path) : props.Value.ContentType;
        return new StorageReadStream(content, count, type, props.Value.ETag.ToString(), stream.CanSeek, props.Value.LastModified);
    }

    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Kind != WriteKind.WriteFile || plan.DryRun) { throw new Unsupported(plan.TargetFolder); }
        var folder = FolderOf(plan.TargetFolder);
        if (folder.Length > 0 && !await HasChild(folder, ct).ConfigureAwait(false)) { throw new NotFound(plan.TargetFolder); }
        var name = Safe(plan.Name);
        var destination = folder.Length == 0 ? name : folder + "/" + name;
        if (plan.Conflict != ConflictPolicy.Overwrite && await container.GetBlobClient(destination).ExistsAsync(ct).ConfigureAwait(false))
        { throw new Conflict(plan.TargetFolder); }
        var stage = ".partials/" + Guid.NewGuid().ToString("N");
        var stream = await container.GetBlobClient(stage).OpenWriteAsync(overwrite: true, cancellationToken: ct).ConfigureAwait(false);
        return new WriteHandle(this, plan, stage, destination, stream);
    }

    private async Task Complete(WritePlan plan, IOperation operation, CancellationToken requested)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(requested, operation.Cancellation);
        var ct = cancellation.Token;
        OperationResult result;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (plan.DryRun)
            {
                _ = await Prepare(plan, ct).ConfigureAwait(false);
                result = new OperationResult.Planned([plan.Kind.ToString()]);
            }
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
        catch (RequestFailedException error) { result = new OperationResult.Failed(Code(error), "The storage change could not be completed."); }
        catch (ArgumentException) { result = new OperationResult.Failed("invalid-plan", "The storage plan is invalid."); }
        await operation.Complete(result).ConfigureAwait(false);
    }

    private async Task<string> Prepare(WritePlan plan, CancellationToken ct)
    {
        if (!Enum.IsDefined(plan.Kind) || !Enum.IsDefined(plan.Conflict)) { throw new Unsupported(plan.Source); }
        var source = plan.Source is { } item ? BlobItemId.Read(item) : null;
        if (source is not null)
        {
            if (source.Length == 0) { throw new Denied(plan.Source); }
            if (!await Exists(source, ct).ConfigureAwait(false)) { throw new NotFound(plan.Source); }
        }
        return plan.Kind switch
        {
            WriteKind.CreateFolder or WriteKind.WriteFile => Join(FolderOf(plan.TargetFolder), Safe(plan.Name)),
            WriteKind.Copy or WriteKind.Move => source is null ? throw new Unsupported(null) : Join(FolderOf(plan.TargetFolder), NameOf(source)),
            WriteKind.Rename => source is null ? throw new Unsupported(null) : Join(Parent(source), Safe(plan.Name)),
            _ => source ?? "",
        };
    }

    private async Task Apply(WritePlan plan, CancellationToken ct)
    {
        if (plan.Kind == WriteKind.WriteFile)
        {
            if (plan.Inline is not { } content) { throw new Unsupported(plan.TargetFolder); }
            await using var writer = await OpenWrite(plan, ct).ConfigureAwait(false);
            await writer.Content.WriteAsync(content, ct).ConfigureAwait(false);
            _ = await writer.Commit(ct).ConfigureAwait(false);
            return;
        }
        var destination = await Prepare(plan, ct).ConfigureAwait(false);
        var source = plan.Source is { } item ? BlobItemId.Read(item) : null;
        if (plan.Kind == WriteKind.Delete)
        {
            await Delete(source!, ct).ConfigureAwait(false);
            return;
        }
        if (plan.Kind == WriteKind.CreateFolder)
        {
            await Put(destination + "/" + FolderMark, ReadOnlyMemory<byte>.Empty, plan.Conflict, ct).ConfigureAwait(false);
            return;
        }
        if (string.Equals(source, destination, StringComparison.Ordinal)) { return; }
        if (await Exists(destination, ct).ConfigureAwait(false))
        {
            if (plan.Conflict != ConflictPolicy.Overwrite) { throw new Conflict(plan.Source ?? plan.TargetFolder); }
            await Delete(destination, ct).ConfigureAwait(false);
        }
        await CopyBytes(source!, destination, ct).ConfigureAwait(false);
        if (plan.Kind is WriteKind.Move or WriteKind.Rename) { await Delete(source!, ct).ConfigureAwait(false); }
    }

    private async Task Commit(string stage, string destination, WritePlan plan, CancellationToken ct)
    {
        var source = container.GetBlobClient(stage);
        var target = container.GetBlobClient(destination);
        var conditions = plan.Conflict == ConflictPolicy.Overwrite ? null : BlobStorageNeuron.CreateOnly();
        try
        {
            try
            {
                var copy = await target.StartCopyFromUriAsync(source.Uri, new BlobCopyFromUriOptions { DestinationConditions = conditions }, ct).ConfigureAwait(false);
                await copy.WaitForCompletionAsync(ct).ConfigureAwait(false);
            }
            catch (RequestFailedException error) when (error.Status is 409 or 412)
            {
                throw new Conflict(plan.TargetFolder, error);
            }
            catch (RequestFailedException)
            {
                await using var read = await source.OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await read.CopyToAsync(buffer, ct).ConfigureAwait(false);
                await target.UploadAsync(BinaryData.FromBytes(buffer.ToArray()), new BlobUploadOptions { Conditions = conditions }, ct).ConfigureAwait(false);
            }
            var copied = await target.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            var staged = await source.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            if (copied.Value.ContentLength != staged.Value.ContentLength) { throw new Unavailable(plan.TargetFolder); }
        }
        finally { await source.DeleteIfExistsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task Put(string path, ReadOnlyMemory<byte> bytes, ConflictPolicy policy, CancellationToken ct)
    {
        var options = new BlobUploadOptions
        {
            Conditions = policy == ConflictPolicy.Overwrite ? null : BlobStorageNeuron.CreateOnly(),
            HttpHeaders = new BlobHttpHeaders { ContentHash = MD5.HashData(bytes.Span), ContentType = ContentType(path) },
        };
        try { await container.GetBlobClient(path).UploadAsync(BinaryData.FromBytes(bytes), options, ct).ConfigureAwait(false); }
        catch (RequestFailedException error) when (error.Status is 409 or 412) { throw new Conflict(null, error); }
    }

    private async Task CopyBytes(string source, string destination, CancellationToken ct)
    {
        var from = container.GetBlobClient(source);
        var to = container.GetBlobClient(destination);
        var props = await from.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        await using var read = await from.OpenReadAsync(cancellationToken: ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (bytes.Length != props.Value.ContentLength) { throw new Unavailable(null); }
        await to.UploadAsync(BinaryData.FromBytes(bytes), new BlobUploadOptions
        {
            Conditions = BlobStorageNeuron.CreateOnly(),
            HttpHeaders = new BlobHttpHeaders { ContentHash = MD5.HashData(bytes), ContentType = props.Value.ContentType },
        }, ct).ConfigureAwait(false);
        var copied = await to.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        if (copied.Value.ContentLength != bytes.Length) { throw new Unavailable(null); }
    }

    private async Task Delete(string path, CancellationToken ct)
    {
        if (!await HasChild(path, ct).ConfigureAwait(false))
        {
            await container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            return;
        }
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, path + "/", ct).ConfigureAwait(false))
        {
            await container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        await container.GetBlobClient(path + "/").DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<List<Item>> Children(string folder, CancellationToken ct)
    {
        var prefix = folder.Length == 0 ? null : folder + "/";
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        await foreach (var entry in container.GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", prefix, ct).ConfigureAwait(false))
        {
            if (entry.IsPrefix)
            {
                var relative = entry.Prefix!.TrimEnd('/');
                var name = NameOf(relative);
                if (Hidden(name)) { continue; }
                items[name] = Folder(relative, name);
            }
            else if (entry.Blob.Name.EndsWith('/'))
            {
                var relative = entry.Blob.Name.TrimEnd('/');
                var name = NameOf(relative);
                if (Hidden(name) || relative == folder) { continue; }
                items.TryAdd(name, Folder(relative, name));
            }
            else
            {
                var name = NameOf(entry.Blob.Name);
                if (Hidden(name) || items.ContainsKey(name)) { continue; }
                items[name] = FileFrom(entry.Blob.Name, name, entry.Blob.Properties.ContentLength, entry.Blob.Properties.LastModified);
            }
        }
        return [.. items.Values];
    }

    private async Task<bool> IsFile(string path, CancellationToken ct)
        => path.Length > 0 && await container.GetBlobClient(path).ExistsAsync(ct).ConfigureAwait(false);

    private async Task<bool> HasChild(string path, CancellationToken ct)
    {
        if (path.Length == 0) { return true; }
        await foreach (var _ in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, path + "/", ct).ConfigureAwait(false)) { return true; }
        return false;
    }

    private async Task<bool> Exists(string path, CancellationToken ct)
        => await IsFile(path, ct).ConfigureAwait(false) || await HasChild(path, ct).ConfigureAwait(false);

    private async Task<Item> FileItem(string path, CancellationToken ct)
    {
        var props = await container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
        return FileFrom(path, NameOf(path), props.Value.ContentLength, props.Value.LastModified);
    }

    private static Item FileFrom(string path, string name, long? size, DateTimeOffset? modified)
        => new(BlobItemId.Of(path), name, ItemKind.File, size, modified, Offered & ~(ItemFlags.Search | ItemFlags.CreateFolder));

    private static Item Folder(string path, string name)
        => new(BlobItemId.Of(path), name, ItemKind.Folder, null, null,
            path.Length == 0 ? ItemFlags.Read | ItemFlags.Search | ItemFlags.Write | ItemFlags.CreateFolder : Offered);

    private string FolderOf(ItemId? id)
    {
        if (id is not { } folder) { throw new Unsupported(null); }
        return BlobItemId.Read(folder);
    }

    private static string Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
        { throw new ArgumentException("The storage plan is invalid."); }
        return name;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;
    private static string Parent(string path) => path.LastIndexOf('/') is var slash && slash < 0 ? "" : path[..slash];
    private static string NameOf(string path) => path.Length == 0 ? "Cloud" : path[(path.LastIndexOf('/') + 1)..];
    private static bool Hidden(string name) => name is ".partials" or ".keep" || name.EndsWith(".intochat-partial", StringComparison.Ordinal);

    private async Task Ensure(CancellationToken ct) => await container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);

    private static string Code(RequestFailedException error) => error.Status switch
    {
        404 => "not-found",
        409 or 412 => "conflict",
        401 => "needs-auth",
        403 => "denied",
        _ => "unavailable",
    };

    private static string ContentType(string path) => System.IO.Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".TXT" or ".LOG" or ".MD" or ".CSV" => "text/plain",
        ".JSON" => "application/json",
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

    private static DigitalBrain.Capabilities.Storage.Page<Item> Cut(List<Item> all, string? cursor, int pageSize)
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
        return new DigitalBrain.Capabilities.Storage.Page<Item>([.. all.Skip(offset).Take(end - offset)],
            end < all.Count ? Convert.ToBase64String(Encoding.UTF8.GetBytes(end.ToString(CultureInfo.InvariantCulture))) : null);
    }

    private Task DeleteStage(string stage, CancellationToken ct)
        => container.GetBlobClient(stage).DeleteIfExistsAsync(cancellationToken: ct);

    private sealed class WriteHandle(BlobStorage owner, WritePlan plan, string stage, string destination, Stream stream) : StorageWriteStream(stream)
    {
        protected override async Task<ItemId> CommitCore(CancellationToken ct)
        {
            await Content.FlushAsync(ct).ConfigureAwait(false);
            await Content.DisposeAsync().ConfigureAwait(false);
            await owner.Commit(stage, destination, plan, ct).ConfigureAwait(false);
            return BlobItemId.Of(destination);
        }

        protected override async Task AbortCore(CancellationToken ct)
        {
            await Content.DisposeAsync().ConfigureAwait(false);
            await owner.DeleteStage(stage, ct).ConfigureAwait(false);
        }
    }

    private sealed record BlobOperation(WritePlan Plan, long? Bytes, string? Sha256) : OperationPlan(Plan.DryRun);

    private sealed class Bounded : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _left;

        public Bounded(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _left = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) { return 0; }
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left <= 0) { return 0; }
            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken).ConfigureAwait(false);
            _left -= read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { _inner.Dispose(); } }
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
