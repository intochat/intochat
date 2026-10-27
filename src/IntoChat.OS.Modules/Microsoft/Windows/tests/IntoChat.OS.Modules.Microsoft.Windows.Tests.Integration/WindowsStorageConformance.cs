using System.Collections.Concurrent;
using System.Text;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.Testing.Conformance.Storage;
using IntoChat.OS.Confirmation;
using IntoChat.OS.Kernel.Composition.Capabilities;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

// 05-08, CAP-03 (D-12): a Windows PC passes the storage@1 kit. Each Connect is a fresh person whose PC allows one fresh folder
// holding the seed, and the storage is the one the runtime broker resolves (Storages), so the fact also proves resolution.
[Trait("host", "own")]
public sealed class WindowsStorageConformance(WindowsMachineHost shared) : StorageConformance<IStorage>, IAsyncDisposable
{
    private string? _root;
    private string? _connection;
    private readonly List<IAsyncDisposable> _listeners = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in _listeners) { await listener.DisposeAsync(); }
    }

    protected override async Task<StorageSubject> Connect(StorageSeed seed, CancellationToken ct)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PC storage runs on Windows only");
        KitRuns.Ran();
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        _root = host.Root;
        _connection = host.Key;
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
            Bound(WindowsStorage.IdOf(Path.Combine(host.Root, @"..\escape.txt"))),
            Bound(WindowsStorage.IdOf(@"C:\Windows\win.ini")),
            Bound(WindowsStorage.IdOf(Path.Combine(revoked, "kept.txt"))),
        ];
        var completed = new CompletedOperations();
        _listeners.Add(await host.Brain.Services.GetRequiredService<IClusterListeners>().Listen<OperationDone>(
            "windows-storage-kit-" + Guid.NewGuid().ToString("N"), (publisher, signal, _) =>
            {
                if (publisher.Brain.Value == host.Owner.BrainId) { completed.Observe(signal); }
                return Task.CompletedTask;
            }));
        return new StorageSubject(new AsOwner(connection.Capability, host.Owner,
            host.Brain.Get<IConfirmationInbox>(IConfirmationInbox.Key(host.Owner.BrainId))), completed, hostile);
    }

    // Inside the allowed root, but never created.
    protected override ItemId MissingId() => Bound(WindowsStorage.IdOf(Path.Combine(_root ?? throw new InvalidOperationException("Connect runs first."), $"missing-{Guid.NewGuid():N}.txt")));

    private ItemId Bound(ItemId id) => new(ConnectionStorage.Root(_connection ?? throw new InvalidOperationException("Connect runs first."))
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(id.Value)));

    // Registered before the first write, including writes whose completion arrives before Write returns its id.
    private sealed class CompletedOperations : IOperationsProbe
    {
        private readonly ConcurrentDictionary<OperationId, TaskCompletionSource<OperationResult>> _results = new();
        private TaskCompletionSource<OperationResult> Result(OperationId id) => _results.GetOrAdd(id,
            static _ => new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        public void Observe(OperationDone signal) => Result(signal.Operation).TrySetResult(signal.Result);
        public Task<OperationResult> WaitFor(OperationId operation, CancellationToken ct) => Result(operation).Task.WaitAsync(ct);
    }
}

// Every kit call as the PC's owner: the person's stamp travels with each call, as it does from an app on their brain.
internal sealed class AsOwner(IStorage inner, Person owner, IConfirmationInbox inbox) : IStorage
{
    public async Task<StorageDescriptor> Describe(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Describe(ct); } }
    public async Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Roots(ct); } }
    public async Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.List(folder, query, ct); } }
    public async Task<Item> Stat(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Stat(id, ct); } }
    public async Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Read(id, range, ct); } }
    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => Approve(() => inner.Write(plan, ct), ct);
    public async Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Search(query, ct); } }
    public async Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.Ancestors(id, ct); } }
    public async Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) { using (owner.AsCaller()) { return await inner.OpenRead(item, range, ct); } }
    public async Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
        => new OwnerWrite(await Approve(() => inner.OpenWrite(plan, ct), ct), owner);

    // The conformance kit explicitly approves its own temporary-file operations as this fresh person's trusted human edge.
    private async Task<T> Approve<T>(Func<Task<T>> call, CancellationToken ct)
    {
        using var caller = owner.AsCaller();
        var result = call();
        while (!result.IsCompleted)
        {
            foreach (var request in (await inbox.Read()).Pending) { await inbox.Resolve(request.PlanId, true); }
            await Task.WhenAny(result, Task.Delay(20, ct));
            ct.ThrowIfCancellationRequested();
        }
        return await result;
    }

    private sealed class OwnerWrite(StorageWriteStream inner, Person owner) : StorageWriteStream(new OwnerStream(inner.Content, owner))
    {
        protected override async Task<ItemId> CommitCore(CancellationToken ct) { using (owner.AsCaller()) { return await inner.Commit(ct); } }
        protected override async Task AbortCore(CancellationToken ct) { using (owner.AsCaller()) { await inner.Abort(ct); } }
    }

    private sealed class OwnerStream(Stream inner, Person owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { using (owner.AsCaller()) { inner.Flush(); } }
        public override async Task FlushAsync(CancellationToken ct) { using (owner.AsCaller()) { await inner.FlushAsync(ct); } }
        public override int Read(byte[] buffer, int offset, int count) { using (owner.AsCaller()) { return inner.Read(buffer, offset, count); } }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { using (owner.AsCaller()) { inner.Write(buffer, offset, count); } }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { using (owner.AsCaller()) { await inner.WriteAsync(buffer, ct); } }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) { using (owner.AsCaller()) { inner.Dispose(); } } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { using (owner.AsCaller()) { await inner.DisposeAsync(); } GC.SuppressFinalize(this); }
    }
}

// Counts the Windows kit facts that ran past the OS skip; WindowsMachineHost checks it when the run ends (Pitfall 11).
internal static class KitRuns
{
    private static int _ran;

    public static int Count => Volatile.Read(ref _ran);

    public static void Ran() => Interlocked.Increment(ref _ran);
}
