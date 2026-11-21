using System.Collections.Concurrent;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;

namespace IntoChat.OS.Tests.E2E.Fixtures.Doubles;

/// <summary>
/// In-memory fake implementation of <see cref="IStorage"/> for opaque-box testing of <see cref="StorageWriteHandler"/>.
/// </summary>
public sealed class FakeStorage : IStorage
{
    private readonly ConcurrentDictionary<ItemId, Item> _items = new();
    private readonly ConcurrentDictionary<ItemId, byte[]> _contents = new();

    public int WriteCount { get; private set; }

    public void SetItem(ItemId id, string name, ItemKind kind = ItemKind.File, string? hash = null, DateTimeOffset? modified = null)
    {
        _items[id] = new Item(
            Id: id,
            Name: name,
            Kind: kind,
            Size: 100L,
            Modified: modified ?? DateTimeOffset.UtcNow,
            Flags: ItemFlags.Read | ItemFlags.Write | ItemFlags.Delete,
            Hash: hash);
    }

    public Task<Item> Stat(ItemId id, CancellationToken ct = default)
    {
        if (_items.TryGetValue(id, out var item))
        {
            return Task.FromResult(item);
        }

        throw new NotFound(id);
    }

    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default)
    {
        WriteCount++;
        if (plan.Source is { } source)
        {
            if (plan.Kind == WriteKind.Delete)
            {
                _items.TryRemove(source, out _);
            }
        }
        else if (plan.Name is { } name)
        {
            var newItemId = new ItemId(Guid.NewGuid().ToString("N"));
            SetItem(newItemId, name, plan.Kind == WriteKind.CreateFolder ? ItemKind.Folder : ItemKind.File, "hash-written");
        }

        return Task.FromResult(new OperationId(Guid.NewGuid().ToString("N")));
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default)
        => Task.FromResult(new StorageDescriptor("FakeStorage", "in-memory", new Quota(QuotaState.Known, 1000L, 100000L, false), ItemFlags.Read | ItemFlags.Write | ItemFlags.Delete));

    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Item>>([.. _items.Values]);

    public Task<Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default)
        => Task.FromResult(new Page<Item>([.. _items.Values], null));

    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default)
    {
        var bytes = _contents.GetOrAdd(id, _ => [1, 2, 3]);
        return Task.FromResult(new Content(bytes, "application/octet-stream", bytes.Length));
    }

    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default)
    {
        var bytes = _contents.GetOrAdd(item, _ => [1, 2, 3]);
        return Task.FromResult(new StorageReadStream(new MemoryStream(bytes), bytes.Length, "application/octet-stream", null, true, DateTimeOffset.UtcNow));
    }

    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default)
    {
        WriteCount++;
        var targetId = plan.Source ?? new ItemId(Guid.NewGuid().ToString("N"));
        return Task.FromResult<StorageWriteStream>(new FakeWriteStream(targetId, bytes =>
        {
            _contents[targetId] = bytes;
            SetItem(targetId, plan.Name ?? "streamed-item", ItemKind.File, "hash-streamed");
        }));
    }

    public Task<Page<Item>> Search(SearchQuery query, CancellationToken ct = default)
        => Task.FromResult(new Page<Item>([.. _items.Values], null));

    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Item>>([]);

    private sealed class FakeWriteStream(ItemId id, Action<byte[]> onCommit) : StorageWriteStream(new MemoryStream())
    {
        protected override Task<ItemId> CommitCore(CancellationToken ct)
        {
            var ms = (MemoryStream)Content;
            onCommit(ms.ToArray());
            return Task.FromResult(id);
        }

        protected override Task AbortCore(CancellationToken ct) => Task.CompletedTask;
    }
}

/// <summary>
/// In-memory implementation of <see cref="IStorageOwnerSource"/> returning <see cref="FakeStorage"/>.
/// </summary>
public sealed class FakeStorageOwnerSource(FakeStorage storage) : IStorageOwnerSource
{
    public StorageOwner For(BrainId brain, string origin)
    {
        return (item, ct) => Task.FromResult(new CapabilityConnection<IStorage>("fake-storage", "FakeStorage", storage));
    }
}

/// <summary>
/// Minimal <see cref="IStorageWriteAuthorizer"/> for testing.
/// </summary>
public sealed class FakeStorageWriteAuthorizer : IStorageWriteAuthorizer
{
    public IDisposable BeginApproved(BrainId brain, string requester) => new NoOpScope();

    public Task<T> Invoke<T>(string connection, WritePlan primitive, Func<Task<T>> send, CancellationToken ct) => send();

    public Task<bool> Consume(NeuronId storageNeuron, WritePlan primitive, CancellationToken ct) => Task.FromResult(true);

    private sealed class NoOpScope : IDisposable
    {
        public void Dispose() { }
    }
}

/// <summary>
/// In-memory fake <see cref="IOperationNeuron"/> to record progress and terminal results.
/// </summary>
public sealed class FakeOperationNeuron : IOperationNeuron
{
    public List<double> ProgressReports { get; } = [];
    public OperationResult? TerminalResult { get; private set; }
    public OperationApproval? RecordedApproval { get; private set; }

    public Task<OperationState> Begin(OperationState initial) => Task.FromResult(initial);

    public Task Prepared(OperationApproval? approval)
    {
        RecordedApproval = approval;
        return Task.CompletedTask;
    }

    public Task<bool> Progress(double fraction)
    {
        ProgressReports.Add(fraction);
        return Task.FromResult(true);
    }

    public Task<OperationState> Complete(OperationResult result)
    {
        TerminalResult = result;
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new OperationState(
            Id: "op-1",
            Brain: "test-brain",
            Origin: "test",
            Kind: "write",
            PlanJson: "{}",
            InlineLength: null,
            InlineHash: null,
            Phase: OperationPhase.Done,
            CancelRequested: false,
            Fraction: 1.0,
            Result: result,
            Started: now,
            Updated: now));
    }

    public Task<bool> RequestCancel() => Task.FromResult(true);

    public Task Reconcile() => Task.CompletedTask;

    public Task<OperationState?> Get() => Task.FromResult<OperationState?>(null);
}
