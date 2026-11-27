using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Capabilities.Storage.Gating;
using DigitalBrain.Confirmation;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Identity.Local.Machine;
using IntoChat.OS.Modules.LocalDisk;

namespace IntoChat.OS.Modules.Microsoft.Windows;

[GenerateSerializer, Alias("windows.connection-state")]
internal sealed record WindowsConnectionState : ConnectionState
{
    [Id(0)] public string[] Roots { get; set; } = [];
}

// The connection to one Windows PC. Its key names the machine; only a silo whose machine id matches serves it,
// and connecting proves the device value is this very machine. As storage, every location is confined
// to the allowed roots, read fresh on every call.
[DigitalBrain.Neurons.Neuron("windows", State = "windows")]
internal sealed class WindowsNeuron(
    MachineId thisMachine, ConfinedDiskCoordinator coordinator, IOperations operations,
    IConfirmations? confirmations = null, IStorageWriteAuthorizer? authorizer = null)
    : ConnectionNeuron<WindowsConnectionState>, IWindows, IStorage, IStorageUpload
{
    private ConfinedDiskStorage? _storage;
    private StorageUploadSessions? _uploads;

    public Task<WindowsFacts> Facts() => Task.FromResult(new WindowsFacts(
        thisMachine.Value, Environment.MachineName, Environment.OSVersion.VersionString, Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

    public Task<string[]> Roots() => Task.FromResult(Snapshot.Roots);

    public async Task AllowRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = PathConfinement.Normalize(path);
        if (!Directory.Exists(full)) { throw new DirectoryNotFoundException($"'{full}' is not a folder on this machine."); }
        if (Snapshot.Roots.Contains(full, StringComparer.OrdinalIgnoreCase)) { return; }
        await SaveQuietly(Snapshot with { Roots = [.. Snapshot.Roots, full] });
    }

    public async Task RevokeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = PathConfinement.Normalize(path);
        await SaveQuietly(Snapshot with
        {
            Roots = Snapshot.Roots.Where(root => !string.Equals(root, full, StringComparison.OrdinalIgnoreCase)).ToArray(),
        });
    }

    // storage@1: the same allowed roots, read fresh on every call, through ConfinedDiskStorage.
    private ConfinedDiskStorage Storage => _storage ??= new(
        () => [.. Snapshot.Roots.Select(LocalRoot.FromPath)],
        operations,
        DriveVolumeSource.Instance,
        coordinator,
        ConfinedDiskShape.Windows);
    private StorageUploadSessions Uploads => _uploads ??= new(() => Storage, UploadCaller, Context.Time);

    private string UploadCaller() => DigitalBrain.Access.UploadScope.Of(Context.Caller);

    Task<StorageDescriptor> IStorage.Describe(CancellationToken ct) => Storage.Describe(ct);
    Task<IReadOnlyList<Item>> IStorage.Roots(CancellationToken ct) => Storage.Roots(ct);
    Task<Page<Item>> IStorage.List(ItemId folder, ListQuery query, CancellationToken ct) => Storage.List(folder, query, ct);
    Task<Item> IStorage.Stat(ItemId id, CancellationToken ct) => Storage.Stat(id, ct);
    Task<Content> IStorage.Read(ItemId id, ByteRange? range, CancellationToken ct) => Storage.Read(id, range, ct);
    async Task<DigitalBrain.Operations.OperationId> IStorage.Write(WritePlan plan, CancellationToken ct)
    {
        await StorageWriteGate.AuthorizeAsync(Context.Id, Storage, plan, Context.Caller, authorizer, confirmations, StorageWritePolicy.AlwaysConfirm, ct).ConfigureAwait(true);
        return await Storage.Write(plan, ct);
    }
    Task<Page<Item>> IStorage.Search(SearchQuery query, CancellationToken ct) => Storage.Search(query, ct);
    Task<IReadOnlyList<Item>> IStorage.Ancestors(ItemId id, CancellationToken ct) => Storage.Ancestors(id, ct);
    Task<StorageReadStream> IStorage.OpenRead(ItemId item, ByteRange? range, CancellationToken ct) => Storage.OpenRead(item, range, ct);
    async Task<StorageWriteStream> IStorage.OpenWrite(WritePlan plan, CancellationToken ct)
    {
        await StorageWriteGate.AuthorizeAsync(Context.Id, Storage, plan, Context.Caller, authorizer, confirmations, StorageWritePolicy.AlwaysConfirm, ct).ConfigureAwait(true);
        return await Storage.OpenWrite(plan, ct);
    }

    async Task<string> IStorageUpload.BeginUpload(WritePlan plan, CancellationToken ct)
    {
        await StorageWriteGate.AuthorizeAsync(Context.Id, Storage, plan, Context.Caller, authorizer, confirmations, StorageWritePolicy.AlwaysConfirm, ct).ConfigureAwait(true);
        return await Uploads.BeginUpload(plan, ct);
    }
    Task IStorageUpload.AppendUpload(string upload, long offset, byte[] bytes, CancellationToken ct) => Uploads.AppendUpload(upload, offset, bytes, ct);
    Task<ItemId> IStorageUpload.CommitUpload(string upload, long length, CancellationToken ct) => Uploads.CommitUpload(upload, length, ct);
    Task IStorageUpload.AbortUpload(string upload, CancellationToken ct) => Uploads.AbortUpload(upload, ct);

    protected override async Task OnDeactivate(DigitalBrain.Neurons.DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (_uploads is not null) { await _uploads.DisposeAsync(); }
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) =>
        Task.FromResult(OperatingSystem.IsWindows() && credential.Value == thisMachine.Value
            ? ConnectionProbeResult.Connected()
            : ConnectionProbeResult.Failing("This device value is not the machine this silo runs on."));

    internal void RequireThisMachine()
    {
        if (Key.Connection != thisMachine.Value)
        { throw new ServiceUnavailableException($"Windows machine '{Key.Connection}' is not this silo's machine."); }
    }

    // An activation for another machine fails before any call (Facts, Connect, Probe) reaches the neuron.
    protected override Task Activated(CancellationToken cancellationToken)
    {
        RequireThisMachine();
        return Task.CompletedTask;
    }
}
