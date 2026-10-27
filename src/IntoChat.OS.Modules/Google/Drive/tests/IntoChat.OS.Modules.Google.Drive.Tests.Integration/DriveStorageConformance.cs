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

    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        _seedFolder = host.KitDrive.Seed(seed);
        return new StorageSubject(await host.ConnectKitAsync(ct), new NoOperations());
    }

    protected override Task<ItemId> SeedFolder(IStorage storage, CancellationToken ct)
        => Task.FromResult(new ItemId(_seedFolder ?? throw new InvalidOperationException("Connect seeds first.")));

    // Drive is read-only in Phase 5: the kit never waits for an operation.
    private sealed class NoOperations : IOperationsProbe
    {
        public Task<OperationResult> WaitFor(OperationId id, CancellationToken ct) => throw new InvalidOperationException("Drive storage starts no operations.");
    }
}

// Every call as the Drive's owner: the person's stamp travels with each call, as it does from an app on their brain.
internal sealed class AsOwner(IStorage inner, Person owner) : IStorage
{
    public async Task<StorageDescriptor> Describe(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Describe(ct); } }
    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Roots(ct); } }
    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.List(folder, query, ct); } }
    public async Task<Item> Stat(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Stat(id, ct); } }
    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Read(id, range, ct); } }
    public async Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Write(plan, ct); } }
    public async Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Search(query, ct); } }
    public async Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Ancestors(id, ct); } }
    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.OpenRead(item, range, ct); } }
    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.OpenWrite(plan, ct); } }
}
