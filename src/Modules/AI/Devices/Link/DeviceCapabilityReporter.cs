using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Link;

// Encapsulates local model discovery, diffing, and compute-capability advertisement + heartbeats.
internal sealed class DeviceCapabilityReporter
{
    private readonly CloudClient _cloud;
    private readonly ILocalRuntime[] _runtimes;
    private readonly MachineId _thisMachine;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly Func<string>? _deviceNameProvider;
    private readonly IOptionsMonitor<DevicesOptions>? _options;
    private readonly IConnectedComputeRouter? _computeRouter;
    private readonly IClientCodeSandboxConsent? _consentStore;
    private readonly IClientResourceConsent? _resourceConsent;
    private readonly ILocalPcResourceExecutor? _localResourceExecutor;
    private readonly IClientEngineeringConsent? _engineeringConsent;

    private readonly SemaphoreSlim _syncGate = new(1, 1);

    private volatile IReadOnlyDictionary<string, (ILocalRuntime Runtime, ModelCard Card)> _advertised =
        new Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)>(StringComparer.OrdinalIgnoreCase);

    private volatile bool _modelsDirty = true;
    private bool _reportedOnce;
    private bool _computeReportedOnce;
    private bool _lastAdvertisedSandboxReady;
    private bool _lastAdvertisedAllowClientCodeSandbox;
    private bool _lastAdvertisedAllowFolders;
    private bool _lastAdvertisedAllowConsole;
    private bool _lastAdvertisedAllowConnectedEngineering;
    private ComputeConsentMode _lastAdvertisedConsentMode = ComputeConsentMode.ExplicitToggle;
    private DateTimeOffset _lastComputeHeartbeatAt = DateTimeOffset.MinValue;

    public DeviceCapabilityReporter(
        CloudClient cloud,
        IEnumerable<ILocalRuntime> runtimes,
        MachineId thisMachine,
        ILogger logger,
        TimeProvider clock,
        Func<string>? deviceNameProvider = null,
        IOptionsMonitor<DevicesOptions>? options = null,
        IConnectedComputeRouter? computeRouter = null,
        IClientCodeSandboxConsent? consentStore = null,
        IClientResourceConsent? resourceConsent = null,
        ILocalPcResourceExecutor? localResourceExecutor = null,
        IClientEngineeringConsent? engineeringConsent = null)
    {
        _cloud = cloud;
        _runtimes = OrderRuntimes(runtimes);
        _thisMachine = thisMachine;
        _logger = logger;
        _clock = clock;
        _deviceNameProvider = deviceNameProvider;
        _options = options;
        _computeRouter = computeRouter;
        _consentStore = consentStore;
        _resourceConsent = resourceConsent;
        _localResourceExecutor = localResourceExecutor;
        _engineeringConsent = engineeringConsent;
    }

    public void MarkModelsDirty() => _modelsDirty = true;

    public void MarkLinkChanged()
    {
        _modelsDirty = true;
        _computeReportedOnce = false;
    }

    public bool TryGetAdvertisedModel(string modelId, out (ILocalRuntime Runtime, ModelCard Card) target)
        => _advertised.TryGetValue(modelId, out target);

    public ComputeCapabilityOptions CurrentComputeOptions()
        => _options?.CurrentValue.Compute ?? new ComputeCapabilityOptions();

    public bool EffectiveAllowClientCodeSandbox(ComputeCapabilityOptions compute)
        => _consentStore is not null
            ? _consentStore.Resolve(compute.AllowClientCodeSandbox)
            : compute.AllowClientCodeSandbox;

    public bool EffectiveAllowWindowsFolderGateway()
        => _resourceConsent?.AllowWindowsFolderGateway ?? false;

    public bool EffectiveAllowConsole()
        => _resourceConsent?.AllowConsole ?? false;

    public bool EffectiveAllowConnectedEngineering()
        => _engineeringConsent?.AllowConnectedEngineering ?? false;

    public ComputeCapabilityAdvertisement BuildComputeCapabilityAdvertisement(LinkSecrets secrets, string[]? consentedFolderLabels = null)
    {
        var compute = CurrentComputeOptions();
        var allowClientCodeSandbox = EffectiveAllowClientCodeSandbox(compute);
        var sandboxReady = compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox);
        var allowFolders = EffectiveAllowWindowsFolderGateway();
        var allowConsole = EffectiveAllowConsole();
        var allowEngineering = EffectiveAllowConnectedEngineering();
        var safeFolderLabels = allowFolders ? consentedFolderLabels : null;
        return new ComputeCapabilityAdvertisement(
            MachineId: _thisMachine.Value,
            DeviceName: DeviceName(),
            ConsentMode: compute.ConsentMode,
            SandboxReady: sandboxReady,
            SandboxRuntime: "unit-host",
            OsDescription: RuntimeInformation.OSDescription,
            MaxConcurrency: compute.MaxConcurrency,
            HeartbeatAt: _clock.GetUtcNow(),
            KeyThumbprint: ComputeKeyThumbprint(secrets.PrivateKeyPkcs8),
            AllowClientCodeSandbox: allowClientCodeSandbox,
            AdvertisedCapabilities: ClientComputeConsentPolicy.CapabilitiesFor(sandboxReady, allowFolders, allowConsole, allowEngineering),
            AllowWindowsFolderGateway: allowFolders,
            AllowConsole: allowConsole,
            ConsentedFolderLabels: safeFolderLabels,
            ConsoleRequiresPersonApproval: true,
            AllowConnectedEngineering: allowEngineering);
    }

    public async Task SyncMetadataAsync(LinkSecrets secrets, CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReportReadyModelsIfNeededAsync(ct).ConfigureAwait(false);
            await ReportComputeCapabilityIfNeededAsync(secrets, ct).ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _advertised = new Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)>(StringComparer.OrdinalIgnoreCase);
            _reportedOnce = false;
            _modelsDirty = true;
            _computeReportedOnce = false;
            _lastAdvertisedSandboxReady = false;
            _lastAdvertisedAllowClientCodeSandbox = false;
            _lastAdvertisedAllowFolders = false;
            _lastAdvertisedAllowConsole = false;
            _lastAdvertisedAllowConnectedEngineering = false;
            _lastComputeHeartbeatAt = DateTimeOffset.MinValue;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task ReportReadyModelsIfNeededAsync(CancellationToken ct)
    {
        var ready = await CollectReadyModelsAsync(ct).ConfigureAwait(false);
        var current = _advertised;
        var changed = !_reportedOnce || _modelsDirty || !SameModelKeys(ready, current);
        if (!changed)
        {
            return;
        }

        var advertised = ready.Values
            .Select(static entry => new AdvertisedModel(
                entry.Card.Id.Value,
                entry.Card.Name,
                entry.Card.Family,
                entry.Card.Can & ~Abilities.Tools))
            .OrderBy(static m => m.Id, StringComparer.Ordinal)
            .ToArray();

        await _cloud.ReportModelsAsync(new ModelsReport(DeviceName(), advertised), ct).ConfigureAwait(false);

        _advertised = ready;
        _reportedOnce = true;
        _modelsDirty = false;
    }

    private async Task ReportComputeCapabilityIfNeededAsync(LinkSecrets secrets, CancellationToken ct)
    {
        var compute = CurrentComputeOptions();
        var now = _clock.GetUtcNow();
        var interval = TimeSpan.FromSeconds(Math.Clamp(compute.HeartbeatIntervalSeconds, 1, 300));
        var allowClientCodeSandbox = EffectiveAllowClientCodeSandbox(compute);
        var sandboxReady = compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox);
        var allowFolders = EffectiveAllowWindowsFolderGateway();
        var allowConsole = EffectiveAllowConsole();
        var allowEngineering = EffectiveAllowConnectedEngineering();
        var anyConsented = sandboxReady || allowFolders || allowConsole || allowEngineering;

        var stateChanged = !_computeReportedOnce
            || _lastAdvertisedSandboxReady != sandboxReady
            || _lastAdvertisedConsentMode != compute.ConsentMode
            || _lastAdvertisedAllowClientCodeSandbox != allowClientCodeSandbox
            || _lastAdvertisedAllowFolders != allowFolders
            || _lastAdvertisedAllowConsole != allowConsole
            || _lastAdvertisedAllowConnectedEngineering != allowEngineering;
        var heartbeatDue = now - _lastComputeHeartbeatAt >= interval;

        if (!stateChanged && !heartbeatDue)
        {
            return;
        }

        // Only send periodic heartbeats while at least one capability is consented; when all are unconsented, send once on initial report or toggle revocation.
        if (!anyConsented && !stateChanged)
        {
            _lastComputeHeartbeatAt = now;
            return;
        }

        string[]? folderLabels = null;
        if (allowFolders && _localResourceExecutor is not null)
        {
            try
            {
                folderLabels = await _localResourceExecutor.GetConsentedFolderLabelsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logger.LogDebug(error, "Could not enumerate consented folder labels for capability heartbeat.");
            }
        }

        var advertisement = BuildComputeCapabilityAdvertisement(secrets, folderLabels);
        await _cloud.ReportComputeCapabilityAsync(advertisement, ct).ConfigureAwait(false);

        if (_computeRouter is not null)
        {
            var ownerBrain = new BrainId(IntoChatOSNames.DefaultOwnerBrainId);
            await _computeRouter.RecordHeartbeatAsync(ownerBrain, advertisement, ct).ConfigureAwait(false);
            if (!anyConsented)
            {
                await _computeRouter.MarkOfflineAsync(ownerBrain, _thisMachine.Value, ct).ConfigureAwait(false);
            }
        }

        _computeReportedOnce = true;
        _lastAdvertisedSandboxReady = sandboxReady;
        _lastAdvertisedAllowClientCodeSandbox = allowClientCodeSandbox;
        _lastAdvertisedAllowFolders = allowFolders;
        _lastAdvertisedAllowConsole = allowConsole;
        _lastAdvertisedAllowConnectedEngineering = allowEngineering;
        _lastAdvertisedConsentMode = compute.ConsentMode;
        _lastComputeHeartbeatAt = now;
    }

    private async Task<Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)>> CollectReadyModelsAsync(CancellationToken ct)
    {
        var ready = new Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)>(StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in _runtimes)
        {
            RuntimeStatus probe;
            try
            {
                probe = await runtime.Probe(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Skipping unreachable local runtime {RuntimeId}.", runtime.Id.Value);
                continue;
            }

            if (!probe.Reachable)
            {
                continue;
            }

            try
            {
                await foreach (var card in runtime.Installed(ct).ConfigureAwait(false))
                {
                    if (!card.Can.HasFlag(Abilities.Text))
                    {
                        continue;
                    }
                    ready.TryAdd(card.Id.Value, (runtime, card));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogDebug(error, "Could not list installed models on runtime {RuntimeId}.", runtime.Id.Value);
            }
        }
        return ready;
    }

    private string DeviceName()
    {
        var custom = _deviceNameProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        return string.IsNullOrWhiteSpace(Environment.MachineName) ? _thisMachine.Value : Environment.MachineName;
    }

    private static string? ComputeKeyThumbprint(byte[]? pkcs8)
    {
        if (pkcs8 is not { Length: > 0 })
        {
            return null;
        }

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return DeviceKeys.Thumbprint(key.ExportSubjectPublicKeyInfo());
    }

    private static bool SameModelKeys(
        IReadOnlyDictionary<string, (ILocalRuntime Runtime, ModelCard Card)> a,
        IReadOnlyDictionary<string, (ILocalRuntime Runtime, ModelCard Card)> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        foreach (var key in a.Keys)
        {
            if (!b.ContainsKey(key))
            {
                return false;
            }
        }
        return true;
    }

    private static ILocalRuntime[] OrderRuntimes(IEnumerable<ILocalRuntime> runtimes)
        => [.. runtimes
            .OrderBy(static r => r.Id == RuntimeId.Ollama ? 0 : r.Id == RuntimeId.FoundryLocal ? 1 : 2)
            .ThenBy(static r => r.Id.Value, StringComparer.Ordinal)];
}
