using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Testing.Conformance.Storage;

namespace IntoChat.OS.Modules.Google.Drive.Tests.Integration;

// 05-09, CAP-03 (D-12, D-16): Google Drive passes the storage@1 kit against the stub. Each Connect seeds a fresh folder under
// My Drive (the 250-item Many folder proves nextPageToken paging), connects a fresh person's Drive and takes the storage the
// runtime broker resolves (Storages). Drive confinement is its OAuth scope, so there are no hostile ids.
[Trait("host", "own")]
public sealed class DriveStorageConformance(DriveHost host) : StorageConformance<IStorage>
{
    private string? _seedFolder;
    private ItemId _missing;

    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        _seedFolder = host.KitDrive.Seed(seed);
        var storage = await host.ConnectKitAsync(ct, DriveScopes.Write);
        var folder = await SeedFolder(storage, ct);
        await host.Operations.WaitFor(await storage.Write(WritePlan.WriteFile(folder, "missing-after-delete", new byte[] { 1 }), ct), ct);
        _missing = (await DriveItems.Find(storage, _seedFolder + "/missing-after-delete", ct)).Id;
        await host.Operations.WaitFor(await storage.Write(WritePlan.Delete(_missing, permanent: true), ct), ct);
        return new StorageSubject(storage, host.Operations, [new ItemId("root")]);
    }

    protected override ItemId MissingId() => _missing;

    protected override async Task<ItemId> SeedFolder(IStorage storage, CancellationToken ct)
        => (await DriveItems.Find(storage, _seedFolder ?? throw new InvalidOperationException("Connect seeds first."), ct)).Id;
}

// Every call as the Drive's owner: the person's stamp travels with each call, as it does from an app on their brain.
internal sealed class AsOwner(IStorage inner, Person owner) : IStorage
{
    internal Person Owner => owner;
    public async Task<StorageDescriptor> Describe(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Describe(ct); } }
    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Roots(ct); } }
    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.List(folder, query, ct); } }
    public async Task<Item> Stat(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Stat(id, ct); } }
    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Read(id, range, ct); } }
    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Write(plan, ct); } }
    public async Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Search(query, ct); } }
    public async Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Ancestors(id, ct); } }
    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        using var caller = owner.AsCaller();
        var read = await inner.OpenRead(item, range, ct);
        return new(new DriveOwnerStream(read.Content, owner), read.Length, read.ContentType, read.ETag, read.CanSeek, read.Modified);
    }
    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        using var caller = owner.AsCaller();
        return new OwnerWrite(await inner.OpenWrite(plan, ct), owner);
    }

    private sealed class OwnerWrite(StorageWriteStream inner, Person owner) : StorageWriteStream(new DriveOwnerStream(inner.Content, owner))
    {
        protected override async Task<ItemId> CommitCore(CancellationToken ct) { using (owner.AsCaller()) { return await inner.Commit(ct); } }
        protected override async Task AbortCore(CancellationToken ct) { using (owner.AsCaller()) { await inner.Abort(ct); } }
    }
}
