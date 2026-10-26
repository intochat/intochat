using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing.Conformance.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

// 05-08, CAP-03 (D-12): a Windows PC passes the storage@1 kit. Each Connect is a fresh person whose PC allows one fresh folder
// holding the seed, and the storage is the one the runtime broker resolves (Storages), so the fact also proves resolution.
[Trait("host", "own")]
public sealed class WindowsStorageConformance(WindowsMachineHost shared) : StorageConformance<IStorage>
{
    private string? _root;

    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PC storage runs on Windows only");
        KitRuns.Ran();
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        _root = host.Root;
        foreach (var entry in seed.Entries)
        {
            var path = Path.Combine(host.Root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Kind == ItemKind.Folder) { Directory.CreateDirectory(path); }
            else { await File.WriteAllBytesAsync(path, entry.Bytes.ToArray(), ct); }
        }
        // A root the person allowed, then revoked: ids under it are outside the confinement again.
        var revoked = Directory.CreateTempSubdirectory("windows-kit-revoked").FullName;
        await File.WriteAllTextAsync(Path.Combine(revoked, "kept.txt"), "kept", ct);
        CapabilityConnection<IStorage> connection;
        using (host.Owner.AsCaller())
        {
            var windows = host.Brain.Get<IWindows>(host.Key);
            await windows.AllowRoot(revoked);
            await windows.RevokeRoot(revoked);
            var broker = host.Brain.Services.GetRequiredService<CapabilityBroker>();
            connection = Assert.Single(await broker.Storages(new DigitalBrain.BrainId(host.Owner.BrainId), ct), item => item.ConnectionKey == host.Key);
        }
        IReadOnlyList<ItemId> hostile =
        [
            WindowsStorage.IdOf(Path.Combine(host.Root, @"..\escape.txt")),
            WindowsStorage.IdOf(@"C:\Windows\win.ini"),
            WindowsStorage.IdOf(Path.Combine(revoked, "kept.txt")),
        ];
        return new StorageSubject(new AsOwner(connection.Capability, host.Owner), new NoOperations(), hostile);
    }

    // Inside the allowed root, but never created.
    protected override ItemId MissingId() => WindowsStorage.IdOf(Path.Combine(_root ?? throw new InvalidOperationException("Connect runs first."), $"missing-{Guid.NewGuid():N}.txt"));

    // Windows offers no Write in Phase 5, so the kit never waits for an operation.
    private sealed class NoOperations : IOperationsProbe
    {
        public Task<OperationResult> WaitFor(OperationId operation, CancellationToken ct) => throw new InvalidOperationException("Windows storage starts no operations.");
    }
}

// Every kit call as the PC's owner: the person's stamp travels with each call, as it does from an app on their brain.
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

// Counts the Windows kit facts that ran past the OS skip; WindowsMachineHost checks it when the run ends (Pitfall 11).
internal static class KitRuns
{
    private static int _ran;

    public static int Count => Volatile.Read(ref _ran);

    public static void Ran() => Interlocked.Increment(ref _ran);
}
