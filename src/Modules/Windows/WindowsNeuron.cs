using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Confirmation;
using DigitalBrain.Errors;
using DigitalBrain.Neurons;
using DigitalBrain.Operations;
using DigitalBrain.Runtime.Orleans.Capabilities;

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
    MachineIdentity identity, WindowsStorageCoordinator coordinator, IOperations operations,
    IConfirmations? confirmations = null, IStorageWriteAuthorizer? authorizer = null)
    : ConnectionNeuron<WindowsConnectionState>, IWindows, IStorage, IStorageUpload
{
    private StorageUploadSessions? _uploads;

    public Task<WindowsFacts> Facts() => Task.FromResult(new WindowsFacts(
        identity.Id, Environment.MachineName, Environment.OSVersion.VersionString, Environment.ProcessorCount,
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

    // storage@1: the same allowed roots, read fresh on every call, through WindowsStorage.
    private WindowsStorage Storage => new(Snapshot.Roots, operations: operations, currentRoots: () => Snapshot.Roots, coordinator: coordinator);
    private StorageUploadSessions Uploads => _uploads ??= new(() => Storage, UploadCaller, Context.Time);

    private static string UploadCaller()
    {
        var caller = RuntimeCallerContext.Require();
        return JsonSerializer.Serialize(new[] { caller.AccountId, caller.BrainId, caller.PrincipalId, caller.Kind.ToString(), caller.AppId, caller.ConversationId });
    }

    Task<StorageDescriptor> IStorage.Describe(CancellationToken ct) => Storage.Describe(ct);
    Task<IReadOnlyList<Item>> IStorage.Roots(CancellationToken ct) => Storage.Roots(ct);
    Task<Page<Item>> IStorage.List(ItemId folder, ListQuery query, CancellationToken ct) => Storage.List(folder, query, ct);
    Task<Item> IStorage.Stat(ItemId id, CancellationToken ct) => Storage.Stat(id, ct);
    Task<Content> IStorage.Read(ItemId id, ByteRange? range, CancellationToken ct) => Storage.Read(id, range, ct);
    async Task<DigitalBrain.Operations.OperationId> IStorage.Write(WritePlan plan, CancellationToken ct)
    {
        await AuthorizeWrite(plan, ct);
        return await Storage.Write(plan, ct);
    }
    Task<Page<Item>> IStorage.Search(SearchQuery query, CancellationToken ct) => Storage.Search(query, ct);
    Task<IReadOnlyList<Item>> IStorage.Ancestors(ItemId id, CancellationToken ct) => Storage.Ancestors(id, ct);
    Task<StorageReadStream> IStorage.OpenRead(ItemId item, ByteRange? range, CancellationToken ct) => Storage.OpenRead(item, range, ct);
    async Task<StorageWriteStream> IStorage.OpenWrite(WritePlan plan, CancellationToken ct)
    {
        await AuthorizeWrite(plan, ct);
        return await Storage.OpenWrite(plan, ct);
    }

    async Task<string> IStorageUpload.BeginUpload(WritePlan plan, CancellationToken ct)
    {
        await AuthorizeWrite(plan, ct);
        return await Uploads.BeginUpload(plan, ct);
    }
    Task IStorageUpload.AppendUpload(string upload, long offset, byte[] bytes, CancellationToken ct) => Uploads.AppendUpload(upload, offset, bytes, ct);
    Task<ItemId> IStorageUpload.CommitUpload(string upload, long length, CancellationToken ct) => Uploads.CommitUpload(upload, length, ct);
    Task IStorageUpload.AbortUpload(string upload, CancellationToken ct) => Uploads.AbortUpload(upload, ct);

    private async Task AuthorizeWrite(WritePlan plan, CancellationToken ct)
    {
        if (authorizer is not null && await authorizer.Consume(Context.Id, plan, ct).ConfigureAwait(false)) { return; }
        if (confirmations is null || plan.DryRun) { return; }
        var descriptor = await Storage.Describe(ct).ConfigureAwait(false);
        var connection = new CapabilityConnection<IStorage>("storage", descriptor.Label, Storage);
        var runner = new StorageWriteRunner((_, _) => Task.FromResult(connection),
            (_, _, _) => throw new InvalidOperationException("A confirmation does not execute writes."));
        var question = await runner.Confirmation(plan, ct).ConfigureAwait(false);
        var answer = await confirmations.Confirm(question, ct).ConfigureAwait(false);
        if (answer.Outcome != ConfirmationOutcome.Approved)
        { throw new Denied(plan.Source ?? plan.TargetFolder); }
    }

    protected override async Task OnDeactivate(DigitalBrain.Neurons.DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (_uploads is not null) { await _uploads.DisposeAsync(); }
    }

    protected override Task<ConnectionProbeResult> ProbeAsync(Credential credential, CancellationToken ct) =>
        Task.FromResult(OperatingSystem.IsWindows() && credential.Value == identity.Id
            ? ConnectionProbeResult.Connected()
            : ConnectionProbeResult.Failing("This device value is not the machine this silo runs on."));

    internal void RequireThisMachine()
    {
        if (Key.Connection != identity.Id)
        { throw new ServiceUnavailableException($"Windows machine '{Key.Connection}' is not this silo's machine."); }
    }

    // An activation for another machine fails before any call (Facts, Connect, Probe) reaches the neuron.
    protected override Task Activated(CancellationToken cancellationToken)
    {
        RequireThisMachine();
        return Task.CompletedTask;
    }
}
