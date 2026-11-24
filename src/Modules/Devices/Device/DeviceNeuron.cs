using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Errors;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Link;

namespace IntoChat.OS.Modules.Devices;

[Neuron("devices.device")]
internal sealed partial class DeviceNeuron(
    MachineId thisMachine,
    IHardwareProbe hardware,
    IEnumerable<ILocalRuntime> runtimes,
    DeviceInstaller installer,
    EdgeAuthority? edge = null,
    DeviceWorker? worker = null) : Neuron<DeviceState>, IDevice, IDeviceInstallReports
{
    [DigitalBrain.Slot(Required = false)]
    private partial DigitalBrain.Components.SlotCollection<IDeviceInstallReports> InstallReports { get; }

    [DigitalBrain.Slot(Key = "main")]
    private partial IModelCatalog ModelCatalog { get; }
    private const int MaxRecent = 20;

    private readonly ILocalRuntime[] _runtimes = OrderRuntimes(runtimes);

    private string KeyMachineId => Id.Key.StartsWith("devices/", StringComparison.Ordinal)
        ? Id.Key["devices/".Length..]
        : Id.Key;

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (!string.Equals(KeyMachineId, thisMachine.Value, StringComparison.Ordinal))
        {
            return;
        }

        // Resume any downloads that were persisted before the host stopped.
        foreach (var entry in State.Queue)
        {
            if (!IsActiveState(entry.State))
            {
                continue;
            }

            var runtime = _runtimes.FirstOrDefault(r => r.Id == entry.Runtime)
                ?? (await ResolveRuntimeAsync(entry.Model, cancellationToken).ConfigureAwait(false))?.Runtime;
            if (runtime is not null)
            {
                installer.Enqueue(Id, entry.Model, runtime, InstallReports[Id]);
            }
        }
    }

    protected override Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken)
    {
        if (cause == DeactivationCause.Shutdown)
        {
            installer.DetachOnShutdown(Id);
        }
        return Task.CompletedTask;
    }

    public async Task<DeviceView> Read()
    {
        EnsureThisMachine();

        var results = _runtimes.Length == 0
            ? []
            : await Task.WhenAll(_runtimes.Select(ReadRuntimeAsync)).ConfigureAwait(false);
        var probes = results.Select(static r => r.Probe).ToArray();
        var installedByRuntime = results
            .Select(static r => (r.Runtime, r.Installed, r.Loaded, r.Catalog))
            .ToList();

        var hw = hardware.Read();
        var activeByModel = State.Queue
            .Where(static e => IsActiveState(e.State))
            .ToDictionary(static e => e.Model.Value, StringComparer.OrdinalIgnoreCase);

        var models = new List<DeviceModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (runtime, installed, loaded, catalog) in installedByRuntime)
        {
            foreach (var card in installed)
            {
                if (!seen.Add(card.Id.Value))
                {
                    continue;
                }
                var state = activeByModel.TryGetValue(card.Id.Value, out var queued)
                    ? queued.State
                    : loaded.Contains(card.Id) ? InstallState.Loaded : InstallState.Ready;
                models.Add(new DeviceModel(card, runtime.Id, state, FitRule.Of(card.Bytes, hw)));
            }

            foreach (var card in catalog)
            {
                if (!seen.Add(card.Id.Value))
                {
                    continue;
                }
                var state = activeByModel.TryGetValue(card.Id.Value, out var queued)
                    ? queued.State
                    : InstallState.Available;
                models.Add(new DeviceModel(card, runtime.Id, state, FitRule.Of(card.Bytes, hw)));
            }
        }

        // Surface any queued entry whose runtime did not return it in Installed or Catalog.
        foreach (var entry in State.Queue)
        {
            if (!IsActiveState(entry.State) || !seen.Add(entry.Model.Value))
            {
                continue;
            }
            var card = KnownModels.Find(entry.Model.Value)
                ?? new ModelCard(entry.Model, AiProvider.Ollama, entry.Runtime.Value, entry.Model.Value, Where.Device, Abilities.Text, Tier.Balanced, Bytes: entry.TotalBytes > 0 ? entry.TotalBytes : null);
            models.Add(new DeviceModel(card, entry.Runtime, entry.State, FitRule.Of(card.Bytes, hw)));
        }

        return new DeviceView(thisMachine.Value, hw, probes, [.. models], [.. State.Queue, .. State.Recent]);
    }

    public Task Install(ModelId model) => EnqueueInstallAsync(model, switchWhenReady: null);

    public async Task Use(ModelRole role, ModelId model)
    {
        EnsureThisMachine();
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        var resolved = await ResolveRuntimeAsync(model, CancellationToken.None).ConfigureAwait(false)
            ?? throw new ArgumentException($"No runtime on this device offers '{model.Value}'.", nameof(model));

        var need = RoleNeeds.Of(role);
        if (!resolved.Card.Can.HasFlag(need))
        {
            throw new ArgumentException($"Model '{model.Value}' cannot serve the {role} role (requires {need}).", nameof(model));
        }

        if (resolved.IsInstalled)
        {
            await SetRoleAsync(role, model).ConfigureAwait(false);
            return;
        }

        await EnqueueInstallAsync(model, switchWhenReady: role, resolved).ConfigureAwait(false);
    }

    public async Task Cancel(ModelId model)
    {
        EnsureThisMachine();
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        var index = FindQueueIndex(model);
        if (index < 0)
        {
            return;
        }

        var existing = State.Queue[index];
        var wasRunning = installer.Cancel(Id, model);
        if (!wasRunning && existing.State == InstallState.Queued)
        {
            var cancelled = existing with { State = InstallState.Cancelled };
            var nextQueue = State.Queue.Where((_, i) => i != index).ToArray();
            var nextRecent = PrependRecent(cancelled);
            await Save(
                State with { Queue = nextQueue, Recent = nextRecent },
                new InstallChanged(model, InstallState.Cancelled, existing.Percent)).ConfigureAwait(false);
        }
    }

    public async Task Remove(ModelId model)
    {
        EnsureThisMachine();
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        var roles = await ReadCatalogRolesAsync().ConfigureAwait(false);
        if (FindAssignedRole(roles, model) is { } assignedRole)
        {
            var displayName = KnownModels.Find(model.Value)?.Name ?? model.Value;
            throw new InvalidOperationException($"'{displayName}' is the {assignedRole} model; pick another first.");
        }

        var resolved = await ResolveRuntimeAsync(model, CancellationToken.None).ConfigureAwait(false)
            ?? throw new ArgumentException($"No runtime on this device offers '{model.Value}'.", nameof(model));

        await resolved.Runtime.Remove(model, CancellationToken.None).ConfigureAwait(false);
        worker?.NotifyModelsChanged();
    }

    public async Task Report(InstallProgress progress)
    {
        EnsureThisMachine();
        ArgumentNullException.ThrowIfNull(progress);

        if (Context.Caller is not { Actor: Actor.Kernel or Actor.Module { Id: DevicesModule.ManifestName } })
        {
            throw new UnauthorizedAccessException("Only the devices module installer may report install progress.");
        }

        var index = FindQueueIndex(progress.Model);
        if (index < 0)
        {
            return;
        }

        var existing = State.Queue[index];
        var clampedPercent = Math.Clamp(progress.Percent, 0d, 100d);
        var isTerminal = progress.State is InstallState.Ready or InstallState.Failed or InstallState.Cancelled;
        var stateChanged = progress.State != existing.State;
        var wholePercentChanged = Math.Floor(clampedPercent) != Math.Floor(existing.Percent);

        if (!stateChanged && !wholePercentChanged && !isTerminal)
        {
            return;
        }

        var updated = existing with
        {
            State = progress.State,
            Percent = clampedPercent,
            Bytes = progress.Bytes > 0 ? progress.Bytes : existing.Bytes,
            TotalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : existing.TotalBytes,
            Error = progress.Error,
        };

        if (!isTerminal)
        {
            var nextQueue = State.Queue.ToArray();
            nextQueue[index] = updated;
            await Save(
                State with { Queue = nextQueue },
                new InstallChanged(updated.Model, updated.State, updated.Percent)).ConfigureAwait(false);
            return;
        }

        var remaining = State.Queue.Where((_, i) => i != index).ToArray();
        var recent = PrependRecent(updated);
        var completedSignal = new InstallChanged(updated.Model, updated.State, updated.Percent);
        await Save(
            State with { Queue = remaining, Recent = recent },
            completedSignal).ConfigureAwait(false);
        worker?.NotifyInstallChanged(completedSignal);

        if (progress.State == InstallState.Ready && existing.SwitchWhenReady is { } targetRole)
        {
            await SetRoleAsync(targetRole, existing.Model).ConfigureAwait(false);
        }
    }

    private async Task EnqueueInstallAsync(ModelId model, ModelRole? switchWhenReady, ResolvedModel? preResolved = null)
    {
        EnsureThisMachine();
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);

        var resolved = preResolved ?? await ResolveRuntimeAsync(model, CancellationToken.None).ConfigureAwait(false)
            ?? throw new ArgumentException($"No runtime on this device offers '{model.Value}'.", nameof(model));

        if (resolved.IsInstalled)
        {
            if (switchWhenReady is { } role)
            {
                await SetRoleAsync(role, model).ConfigureAwait(false);
            }
            return;
        }

        var existingIndex = FindQueueIndex(model);
        if (existingIndex >= 0)
        {
            if (switchWhenReady is { } role && State.Queue[existingIndex].SwitchWhenReady != role)
            {
                var updated = State.Queue.ToArray();
                updated[existingIndex] = updated[existingIndex] with { SwitchWhenReady = role };
                await Save(
                    State with { Queue = updated },
                    new InstallChanged(model, updated[existingIndex].State, updated[existingIndex].Percent)).ConfigureAwait(false);
            }
            installer.Enqueue(Id, model, resolved.Runtime, InstallReports[Id]);
            return;
        }

        var activeNow = installer.Enqueue(Id, model, resolved.Runtime, InstallReports[Id]);
        var initialState = activeNow ? InstallState.Downloading : InstallState.Queued;
        var entry = new Install(
            Model: model,
            Runtime: resolved.Runtime.Id,
            State: initialState,
            Percent: 0,
            Bytes: 0,
            TotalBytes: resolved.Card.Bytes ?? 0,
            Error: null,
            SwitchWhenReady: switchWhenReady);

        await Save(
            State with { Queue = [.. State.Queue, entry] },
            new InstallChanged(model, initialState, 0)).ConfigureAwait(false);
    }

    private async Task SetRoleAsync(ModelRole role, ModelId model)
    {
        using var scope = edge?.Enter(edge.Module(DevicesModule.ManifestName, Id.Brain));
        await ModelCatalog.SetRole(role, model).ConfigureAwait(false);
    }

    private Task<RoleMap> ReadCatalogRolesAsync()
        => ModelCatalog.Roles();

    private static ModelRole? FindAssignedRole(RoleMap roles, ModelId model)
    {
        foreach (var (role, assigned) in roles.Device)
        {
            if (string.Equals(assigned.Value, model.Value, StringComparison.OrdinalIgnoreCase))
            {
                return role;
            }
        }
        foreach (var (role, assigned) in roles.Cloud)
        {
            if (string.Equals(assigned.Value, model.Value, StringComparison.OrdinalIgnoreCase))
            {
                return role;
            }
        }
        return null;
    }

    private sealed record ResolvedModel(ILocalRuntime Runtime, ModelCard Card, bool IsInstalled);

    private async Task<ResolvedModel?> ResolveRuntimeAsync(ModelId model, CancellationToken ct)
    {
        foreach (var runtime in _runtimes)
        {
            var installed = await CollectSafeAsync(runtime.Installed(ct), runtime.Id, "installed").ConfigureAwait(false);
            var hit = installed.FirstOrDefault(c => string.Equals(c.Id.Value, model.Value, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return new ResolvedModel(runtime, hit, IsInstalled: true);
            }

            var catalog = await CollectSafeAsync(runtime.Catalog(ct), runtime.Id, "catalog").ConfigureAwait(false);
            var catalogHit = catalog.FirstOrDefault(c => string.Equals(c.Id.Value, model.Value, StringComparison.OrdinalIgnoreCase));
            if (catalogHit is not null)
            {
                return new ResolvedModel(runtime, catalogHit, IsInstalled: false);
            }
        }
        return null;
    }

    private int FindQueueIndex(ModelId model)
    {
        for (var i = 0; i < State.Queue.Length; i++)
        {
            if (string.Equals(State.Queue[i].Model.Value, model.Value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private Install[] PrependRecent(Install finished)
        => [finished, .. State.Recent.Take(MaxRecent - 1)];

    private void EnsureThisMachine()
    {
        if (!string.Equals(KeyMachineId, thisMachine.Value, StringComparison.Ordinal))
        {
            throw new ServiceUnavailableException("This device is not this silo's machine.");
        }
    }

    private static bool IsActiveState(InstallState state)
        => state is InstallState.Queued or InstallState.Downloading or InstallState.Verifying;

    private static ILocalRuntime[] OrderRuntimes(IEnumerable<ILocalRuntime> runtimes)
        => [.. runtimes.OrderBy(static r => r.Id == RuntimeId.Ollama ? 0 : r.Id == RuntimeId.FoundryLocal ? 1 : 2)
            .ThenBy(static r => r.Id.Value, StringComparer.Ordinal)];

    private static async Task<(ILocalRuntime Runtime, RuntimeStatus Probe, List<ModelCard> Installed, HashSet<ModelId> Loaded, List<ModelCard> Catalog)> ReadRuntimeAsync(ILocalRuntime runtime)
    {
        var probe = await SafeProbeAsync(runtime).ConfigureAwait(false);
        var installed = await CollectSafeAsync(runtime.Installed(CancellationToken.None), runtime.Id, "installed").ConfigureAwait(false);
        var loaded = await SafeLoadedAsync(runtime).ConfigureAwait(false);
        var catalog = await CollectSafeAsync(runtime.Catalog(CancellationToken.None), runtime.Id, "catalog").ConfigureAwait(false);
        return (runtime, probe, installed, loaded, catalog);
    }

    private static async Task<RuntimeStatus> SafeProbeAsync(ILocalRuntime runtime)
    {
        try
        {
            return await runtime.Probe(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<DeviceNeuron>().LogSwallowed(error, $"probing runtime '{runtime.Id.Value}'");
            return new RuntimeStatus(runtime.Id, Reachable: false, Version: null, Endpoint: null);
        }
    }

    private static async Task<HashSet<ModelId>> SafeLoadedAsync(ILocalRuntime runtime)
    {
        try
        {
            var loaded = await runtime.Loaded(CancellationToken.None).ConfigureAwait(false);
            return [.. loaded];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<DeviceNeuron>().LogSwallowed(error, $"reading loaded models from runtime '{runtime.Id.Value}'");
            return [];
        }
    }

    private static async Task<List<ModelCard>> CollectSafeAsync(IAsyncEnumerable<ModelCard> source, RuntimeId runtimeId, string kind)
    {
        var list = new List<ModelCard>();
        try
        {
            await foreach (var card in source.ConfigureAwait(false))
            {
                list.Add(card);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<DeviceNeuron>().LogSwallowed(error, $"reading {kind} models from runtime '{runtimeId.Value}'");
        }
        return list;
    }
}
