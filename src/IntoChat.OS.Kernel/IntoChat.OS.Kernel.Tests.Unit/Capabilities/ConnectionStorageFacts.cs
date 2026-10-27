using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Kernel.Composition.Capabilities;

namespace IntoChat.OS.Kernel.Tests.Unit.Capabilities;

public sealed class ConnectionStorageFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EqualProviderIdsRemainBoundToTheirConnection()
    {
        var backing = new InMemoryStorage();
        var left = new ConnectionStorage(backing, "owner/connections/drive/left");
        var right = new ConnectionStorage(backing, "owner/connections/drive/right");
        var a = Assert.Single(await left.Roots(Ct));
        var b = Assert.Single(await right.Roots(Ct));
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(a.Id, (await left.Stat(a.Id, Ct)).Id);
        await Assert.ThrowsAsync<Denied>(() => left.Stat(b.Id, Ct));
        await Assert.ThrowsAsync<Denied>(() => left.Write(WritePlan.CreateFolder(b.Id, "wrong"), Ct));
        await Assert.ThrowsAsync<Denied>(() => left.Stat(backing.Root, Ct));
    }

    [Fact]
    public async Task UploadTransfersChunksAndAbortedFilesStayInvisible()
    {
        var backing = new InMemoryStorage();
        await using var uploads = new StorageUploadSessions(() => backing, () => "authenticated-app");
        var storage = new ConnectionStorage(backing, "owner/connections/drive/left", uploads);
        var root = Assert.Single(await storage.Roots(Ct));
        var bytes = new byte[900_001];
        new Random(71).NextBytes(bytes);
        await using (var abandoned = await storage.OpenWrite(WritePlan.WriteFile(root.Id, "abandoned.bin"), Ct))
        { await abandoned.Content.WriteAsync(bytes, Ct); }
        Assert.Empty((await storage.List(root.Id, new(), Ct)).Items);
        await using var write = await storage.OpenWrite(WritePlan.WriteFile(root.Id, "kept.bin"), Ct);
        await write.Content.WriteAsync(bytes, Ct);
        var id = await write.Commit(Ct);
        Assert.Equal(bytes, (await storage.Read(id, null, Ct)).Bytes.ToArray());
        Assert.StartsWith(ConnectionStorage.Root("owner/connections/drive/left"), id.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingReadUsesBoundedRangesAndPreservesAllBytes()
    {
        var backing = new InMemoryStorage();
        var bytes = new byte[900_001];
        new Random(71).NextBytes(bytes);
        var id = backing.AddFile(backing.Root, "large.bin", bytes);
        var remote = new TrackingStorage(backing);
        var storage = new ConnectionStorage(remote);
        await using var read = await storage.OpenRead(id, null, Ct);
        using var output = new MemoryStream();
        await read.Content.CopyToAsync(output, Ct);
        Assert.Equal(bytes, output.ToArray());
        Assert.True(remote.Reads > 1);
        Assert.InRange(remote.LargestRead, 1, 262144);
    }

    private sealed class TrackingStorage(IStorage inner) : IStorage
    {
        public long LargestRead { get; private set; }
        public int Reads { get; private set; }
        public Task<StorageDescriptor> Describe(CancellationToken ct = default) => inner.Describe(ct);
        public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => inner.Roots(ct);
        public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => inner.List(folder, query, ct);
        public Task<Item> Stat(ItemId id, CancellationToken ct = default) => inner.Stat(id, ct);
        public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
        {
            Assert.NotNull(range);
            Reads++;
            LargestRead = Math.Max(LargestRead, range.Value.Length ?? long.MaxValue);
            return inner.Read(id, range, ct);
        }
        public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => inner.Write(plan, ct);
        public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => inner.Search(query, ct);
        public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => inner.Ancestors(id, ct);
        public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => inner.OpenRead(item, range, ct);
        public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => inner.OpenWrite(plan, ct);
    }
}
