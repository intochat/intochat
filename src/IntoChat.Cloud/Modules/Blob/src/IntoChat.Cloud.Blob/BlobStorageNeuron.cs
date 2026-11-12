using Azure;
using Azure.Storage.Blobs.Models;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Operations;

namespace IntoChat.Cloud.Blob;

// Rename and move copy then delete. That is not atomic: a failed delete keeps the source beside the copy.
[DigitalBrain.Neurons.Neuron("intochat.cloud.blob", State = "intochat.cloud.blob")]
public sealed class BlobStorageNeuron(IOperations operations) : DigitalBrain.Neurons.Neuron<BlobStorageState>, IStorage
{
    internal const string TypeName = "intochat.cloud.blob";

    internal static BlobRequestConditions CreateOnly() => new() { IfNoneMatch = ETag.All };

    private BlobStorage Engine => new(Container(), operations);

    private Azure.Storage.Blobs.BlobContainerClient Container()
    {
        var connection = State.ConnectionString;
        if (string.IsNullOrWhiteSpace(connection)) { throw new NeedsAuth(null); }
        var service = BlobStorage.Open(connection);
        return service.GetBlobContainerClient(BlobContainerName.For(State.BrainId.Length == 0 ? Id.Brain.Value : State.BrainId));
    }

    public Task<StorageDescriptor> Describe(CancellationToken ct = default) => Engine.Describe(ct);
    public Task<IReadOnlyList<Item>> Roots(CancellationToken ct = default) => Engine.Roots(ct);
    public Task<DigitalBrain.Capabilities.Storage.Page<Item>> List(ItemId folder, ListQuery query, CancellationToken ct = default) => Engine.List(folder, query, ct);
    public Task<Item> Stat(ItemId id, CancellationToken ct = default) => Engine.Stat(id, ct);
    public Task<Content> Read(ItemId id, ByteRange? range, CancellationToken ct = default) => Engine.Read(id, range, ct);
    public Task<OperationId> Write(WritePlan plan, CancellationToken ct = default) => Engine.Write(plan, ct);
    public Task<DigitalBrain.Capabilities.Storage.Page<Item>> Search(SearchQuery query, CancellationToken ct = default) => Engine.Search(query, ct);
    public Task<IReadOnlyList<Item>> Ancestors(ItemId id, CancellationToken ct = default) => Engine.Ancestors(id, ct);
    public Task<StorageReadStream> OpenRead(ItemId item, ByteRange? range, CancellationToken ct = default) => Engine.OpenRead(item, range, ct);
    public Task<StorageWriteStream> OpenWrite(WritePlan plan, CancellationToken ct = default) => Engine.OpenWrite(plan, ct);
}

public sealed class BlobStorageState
{
    public string BrainId { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}
