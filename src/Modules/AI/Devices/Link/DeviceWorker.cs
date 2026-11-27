using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Link;

// Background service on the PC: advertises Ready local models and local sandbox compute capability + heartbeats,
// and supervises independent concurrent long-poll lanes for chat (/circle/v1/compute/next),
// scoped Plane 1 sandbox jobs (/circle/v1/compute/sandbox/next), and Mode B PC resource jobs
// (/circle/v1/compute/resource/next) so a 25 s idle poll or transient error on one lane never starves another.
internal sealed class DeviceWorker : BackgroundService
{
    private static readonly ActivitySource Telemetry = new("IntoChat.OS.Modules.Devices");
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan InitialBackoffDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);

    private readonly CloudClient _cloud;
    private readonly DeviceCredentials _credentials;
    private readonly MachineId _thisMachine;
    private readonly ILogger<DeviceWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<string>? _deviceNameProvider;
    private readonly IServiceProvider? _services;
    private readonly IOptionsMonitor<DevicesOptions>? _options;
    private readonly IConnectedComputeRouter? _computeRouter;
    private readonly ILocalSandboxExecutor? _localSandbox;
    private readonly IClientCodeSandboxConsent? _consentStore;
    private readonly IClientResourceConsent? _resourceConsent;
    private readonly ILocalPcResourceExecutor? _localResourceExecutor;
    private readonly IClientEngineeringConsent? _engineeringConsent;
    private readonly ILocalEngineeringExecutor? _localEngineeringExecutor;
    private readonly Func<double> _jitterProvider;

    private readonly ILocalRuntime[] _runtimes;
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _heartbeatWake = new(0, 1);
    private readonly SemaphoreSlim _engineeringConsentWake = new(0, 1);
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

    public DeviceWorker(
        CloudClient cloud,
        DeviceCredentials credentials,
        IEnumerable<ILocalRuntime> runtimes,
        MachineId thisMachine,
        ILogger<DeviceWorker> logger,
        TimeProvider? clock = null,
        Func<string>? deviceNameProvider = null,
        IServiceProvider? services = null,
        IOptionsMonitor<DevicesOptions>? options = null,
        IConnectedComputeRouter? computeRouter = null,
        ILocalSandboxExecutor? localSandbox = null,
        IClientCodeSandboxConsent? consentStore = null,
        IClientResourceConsent? resourceConsent = null,
        ILocalPcResourceExecutor? localResourceExecutor = null,
        Func<double>? jitterProvider = null,
        IClientEngineeringConsent? engineeringConsent = null,
        ILocalEngineeringExecutor? localEngineeringExecutor = null)
    {
        _cloud = cloud;
        _credentials = credentials;
        _runtimes = OrderRuntimes(runtimes);
        _thisMachine = thisMachine;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _deviceNameProvider = deviceNameProvider;
        _services = services;
        _options = options;
        _computeRouter = computeRouter;
        _localSandbox = localSandbox;
        _consentStore = consentStore;
        _resourceConsent = resourceConsent;
        _localResourceExecutor = localResourceExecutor;
        _jitterProvider = jitterProvider ?? DefaultJitter;
        _engineeringConsent = engineeringConsent;
        _localEngineeringExecutor = localEngineeringExecutor;

        _consentStore?.Changed += OnSandboxConsentChanged;
        _resourceConsent?.Changed += OnResourceConsentChanged;
        _engineeringConsent?.Changed += OnEngineeringConsentChanged;
    }

    internal void NotifyLinkChanged()
    {
        _modelsDirty = true;
        _computeReportedOnce = false;
        ReleaseSignal(_wakeSignal);
        ReleaseSignal(_heartbeatWake);
        ReleaseSignal(_engineeringConsentWake);
    }

    internal void NotifyInstallChanged(InstallChanged signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.State is InstallState.Ready or InstallState.Cancelled or InstallState.Failed)
        {
            _modelsDirty = true;
            ReleaseSignal(_heartbeatWake);
        }
    }

    internal void NotifyModelsChanged()
    {
        _modelsDirty = true;
        ReleaseSignal(_heartbeatWake);
    }

    public override void Dispose()
    {
        _consentStore?.Changed -= OnSandboxConsentChanged;
        _resourceConsent?.Changed -= OnResourceConsentChanged;
        _engineeringConsent?.Changed -= OnEngineeringConsentChanged;

        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sessionFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var secrets = await _credentials.Read(stoppingToken).ConfigureAwait(false);
                if (secrets is null)
                {
                    await ResetAdvertisedAsync(stoppingToken).ConfigureAwait(false);
                    sessionFailures = 0;
                    await DelayOrWakeAsync(_wakeSignal, IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await SyncMetadataAsync(secrets, stoppingToken).ConfigureAwait(false);
                sessionFailures = 0;

                var revoked = await RunLinkedSessionAsync(stoppingToken).ConfigureAwait(false);
                if (revoked)
                {
                    await ResetAdvertisedAsync(CancellationToken.None).ConfigureAwait(false);
                    await PersistRevokedStatusBestEffortAsync().ConfigureAwait(false);
                    await DelayOrWakeAsync(_wakeSignal, ErrorDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceLinkRevokedException error)
            {
                _logger.LogInformation(error, "Device worker stopping poll: cloud credential was revoked.");
                await ResetAdvertisedAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistRevokedStatusBestEffortAsync().ConfigureAwait(false);
                sessionFailures = 0;
                await DelayOrWakeAsync(_wakeSignal, ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                sessionFailures++;
                var delay = ComputeBackoffDelay(sessionFailures, _jitterProvider);
                _logger.LogWarning(error, "Device worker could not reach cloud; retrying in {DelayMs:F0} ms.", delay.TotalMilliseconds);
                _modelsDirty = true;
                await DelayOrWakeAsync(_wakeSignal, delay, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException error) when (!stoppingToken.IsCancellationRequested)
            {
                sessionFailures++;
                var delay = ComputeBackoffDelay(sessionFailures, _jitterProvider);
                _logger.LogWarning(error, "Device worker cloud call timed out; retrying in {DelayMs:F0} ms.", delay.TotalMilliseconds);
                _modelsDirty = true;
                await DelayOrWakeAsync(_wakeSignal, delay, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                sessionFailures++;
                var delay = ComputeBackoffDelay(sessionFailures, _jitterProvider);
                _logger.LogWarning(error, "Device worker encountered an unexpected error; retrying in {DelayMs:F0} ms.", delay.TotalMilliseconds);
                _modelsDirty = true;
                await DelayOrWakeAsync(_wakeSignal, delay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    // One deterministic cycle: ensure Ready models and compute capability heartbeat are reported,
    // then poll all active lanes concurrently so a slow poll on one lane does not serialize the others.
    internal async Task Tick(CancellationToken ct = default)
    {
        var secrets = await _credentials.Read(ct).ConfigureAwait(false);
        if (secrets is null)
        {
            await ResetAdvertisedAsync(ct).ConfigureAwait(false);
            return;
        }

        await SyncMetadataAsync(secrets, ct).ConfigureAwait(false);

        var laneTasks = new List<Task>(4)
        {
            PollAndExecuteChatOnceAsync(ct),
        };

        if (_localSandbox is not null)
        {
            laneTasks.Add(PollAndExecuteSandboxOnceAsync(ct));
        }

        if (_localResourceExecutor is not null)
        {
            laneTasks.Add(PollAndExecuteResourceOnceAsync(ct));
        }

        if (_localEngineeringExecutor is not null)
        {
            laneTasks.Add(PollAndExecuteEngineeringOnceAsync(ct));
        }

        await Task.WhenAll(laneTasks).ConfigureAwait(false);
    }

    internal static TimeSpan ComputeBackoffDelay(int consecutiveFailures, Func<double>? jitterProvider = null)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var rawMs = InitialBackoffDelay.TotalMilliseconds * (1 << exponent);
        var cappedMs = Math.Min(rawMs, MaxBackoffDelay.TotalMilliseconds);
        var sample = Math.Clamp((jitterProvider ?? DefaultJitter)(), 0.0, 1.0);
        var jitterFactor = 0.75 + (0.5 * sample);
        var jitteredMs = Math.Min(cappedMs * jitterFactor, MaxBackoffDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(1.0, jitteredMs));
    }

    private static double DefaultJitter() => Random.Shared.NextDouble();

    internal ComputeCapabilityAdvertisement BuildComputeCapabilityAdvertisement(LinkSecrets secrets, string[]? consentedFolderLabels = null)
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

    private async Task<bool> RunLinkedSessionAsync(CancellationToken stoppingToken)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sessionCt = sessionCts.Token;
        var revokedFlag = 0;

        void SignalRevoked(DeviceLinkRevokedException error, string lane)
        {
            if (Interlocked.Exchange(ref revokedFlag, 1) == 0)
            {
                _logger.LogInformation(error, "Device worker lane '{Lane}' detected credential revocation; stopping active lanes.", lane);
                sessionCts.Cancel();
            }
        }

        void SignalUnlinked()
        {
            sessionCts.Cancel();
        }

        var supervisedTasks = new List<Task>(4)
        {
            RunHeartbeatLoopAsync(sessionCt, SignalRevoked, SignalUnlinked),
            RunLaneLoopAsync("chat", PollAndExecuteChatOnceAsync, sessionCt, SignalRevoked),
        };

        if (_localSandbox is not null)
        {
            supervisedTasks.Add(RunLaneLoopAsync("sandbox", PollAndExecuteSandboxOnceAsync, sessionCt, SignalRevoked));
        }

        if (_localResourceExecutor is not null)
        {
            supervisedTasks.Add(RunLaneLoopAsync("resource", PollAndExecuteResourceOnceAsync, sessionCt, SignalRevoked));
        }

        if (_localEngineeringExecutor is not null)
        {
            supervisedTasks.Add(RunLaneLoopAsync("engineering", PollAndExecuteEngineeringWhenConsentedOnceAsync, sessionCt, SignalRevoked));
        }

        await Task.WhenAll(supervisedTasks).ConfigureAwait(false);
        return Volatile.Read(ref revokedFlag) != 0;
    }

    private async Task RunHeartbeatLoopAsync(
        CancellationToken sessionCt,
        Action<DeviceLinkRevokedException, string> onRevoked,
        Action onUnlinked)
    {
        var consecutiveFailures = 0;

        while (!sessionCt.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(CurrentComputeOptions().HeartbeatIntervalSeconds, 1, 300));
            await DelayOrWakeQuietlyAsync(_heartbeatWake, interval, sessionCt).ConfigureAwait(false);
            if (sessionCt.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var secrets = await _credentials.Read(sessionCt).ConfigureAwait(false);
                if (secrets is null)
                {
                    onUnlinked();
                    break;
                }

                await SyncMetadataAsync(secrets, sessionCt).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceLinkRevokedException error)
            {
                onRevoked(error, "heartbeat");
                break;
            }
            catch (Exception error)
            {
                consecutiveFailures++;
                var backoff = ComputeBackoffDelay(consecutiveFailures, _jitterProvider);
                _modelsDirty = true;
                _logger.LogWarning(
                    error,
                    "Device worker lane 'heartbeat' failed (attempt {Attempt}); backing off {DelayMs:F0} ms.",
                    consecutiveFailures,
                    backoff.TotalMilliseconds);
                await DelayOrWakeQuietlyAsync(_heartbeatWake, backoff, sessionCt).ConfigureAwait(false);
            }
        }
    }

    private async Task RunLaneLoopAsync(
        string laneName,
        Func<CancellationToken, Task> pollAndExecuteOnce,
        CancellationToken sessionCt,
        Action<DeviceLinkRevokedException, string> onRevoked)
    {
        var consecutiveFailures = 0;

        while (!sessionCt.IsCancellationRequested)
        {
            try
            {
                await pollAndExecuteOnce(sessionCt).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (sessionCt.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceLinkRevokedException error)
            {
                onRevoked(error, laneName);
                break;
            }
            catch (HttpRequestException error)
            {
                consecutiveFailures++;
                var backoff = ComputeBackoffDelay(consecutiveFailures, _jitterProvider);
                _logger.LogWarning(
                    error,
                    "Device worker lane '{Lane}' could not reach cloud (attempt {Attempt}); backing off {DelayMs:F0} ms.",
                    laneName,
                    consecutiveFailures,
                    backoff.TotalMilliseconds);
                await DelayQuietlyAsync(backoff, sessionCt).ConfigureAwait(false);
            }
            catch (TaskCanceledException error) when (!sessionCt.IsCancellationRequested)
            {
                consecutiveFailures++;
                var backoff = ComputeBackoffDelay(consecutiveFailures, _jitterProvider);
                _logger.LogWarning(
                    error,
                    "Device worker lane '{Lane}' cloud call timed out (attempt {Attempt}); backing off {DelayMs:F0} ms.",
                    laneName,
                    consecutiveFailures,
                    backoff.TotalMilliseconds);
                await DelayQuietlyAsync(backoff, sessionCt).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                consecutiveFailures++;
                var backoff = ComputeBackoffDelay(consecutiveFailures, _jitterProvider);
                _logger.LogWarning(
                    error,
                    "Device worker lane '{Lane}' encountered an unexpected error (attempt {Attempt}); backing off {DelayMs:F0} ms.",
                    laneName,
                    consecutiveFailures,
                    backoff.TotalMilliseconds);
                await DelayQuietlyAsync(backoff, sessionCt).ConfigureAwait(false);
            }
        }
    }

    private async Task PollAndExecuteChatOnceAsync(CancellationToken ct)
    {
        var job = await _cloud.PollNextAsync(ct).ConfigureAwait(false);
        if (job is not null)
        {
            await ExecuteJobAsync(job, ct).ConfigureAwait(false);
        }
    }

    private async Task PollAndExecuteSandboxOnceAsync(CancellationToken ct)
    {
        var sandboxJob = await _cloud.PollNextSandboxAsync(ct).ConfigureAwait(false);
        if (sandboxJob is not null)
        {
            await ExecuteSandboxJobAsync(sandboxJob, ct).ConfigureAwait(false);
        }
    }

    private async Task PollAndExecuteResourceOnceAsync(CancellationToken ct)
    {
        var resourceJob = await _cloud.PollNextResourceAsync(ct).ConfigureAwait(false);
        if (resourceJob is not null)
        {
            await ExecuteResourceJobAsync(resourceJob, ct).ConfigureAwait(false);
        }
    }

    private async Task PollAndExecuteEngineeringWhenConsentedOnceAsync(CancellationToken ct)
    {
        if (!EffectiveAllowConnectedEngineering())
        {
            await _engineeringConsentWake.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        await PollAndExecuteEngineeringOnceAsync(ct).ConfigureAwait(false);
    }

    private async Task PollAndExecuteEngineeringOnceAsync(CancellationToken ct)
    {
        var engineeringJob = await _cloud.PollNextEngineeringAsync(ct).ConfigureAwait(false);
        if (engineeringJob is not null)
        {
            await ExecuteEngineeringJobAsync(engineeringJob, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncMetadataAsync(LinkSecrets secrets, CancellationToken ct)
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

    private async Task ExecuteResourceJobAsync(PcResourceRequest job, CancellationToken ct)
    {
        var isConsole = string.Equals(job.Capability, ClientComputeConsentPolicy.ConsoleCapability, StringComparison.Ordinal);
        var isFolder = string.Equals(job.Capability, ClientComputeConsentPolicy.WindowsFolderGatewayCapability, StringComparison.Ordinal);
        var allowFolders = EffectiveAllowWindowsFolderGateway();
        var allowConsole = EffectiveAllowConsole();

        if (!isConsole && !isFolder)
        {
            _logger.LogInformation("Refused Mode B resource job {JobId}: unsupported capability '{Capability}'.", job.JobId, job.Capability);
            await _cloud.ReportResourceResultAsync(
                job.JobId,
                new PcResourceResponse(
                    Ok: false,
                    Error: $"unsupported-capability: '{job.Capability}' is not supported on this PC.",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (isFolder && !allowFolders)
        {
            _logger.LogInformation("Refused Mode B folder job {JobId}: IWindowsFolderGateway consent is OFF.", job.JobId);
            await _cloud.ReportResourceResultAsync(
                job.JobId,
                new PcResourceResponse(
                    Ok: false,
                    Error: "consent-disabled: IWindowsFolderGateway is disabled on this PC.",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && !allowConsole)
        {
            _logger.LogInformation("Refused Mode B console job {JobId}: IConsole consent is OFF.", job.JobId);
            await _cloud.ReportResourceResultAsync(
                job.JobId,
                new PcResourceResponse(
                    Ok: false,
                    Error: "consent-disabled: IConsole is disabled on this PC.",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && string.IsNullOrWhiteSpace(job.ApprovalToken))
        {
            _logger.LogInformation("Refused Mode B console job {JobId}: missing [PersonOnly] approval proof.", job.JobId);
            await _cloud.ReportResourceResultAsync(
                job.JobId,
                new PcResourceResponse(
                    Ok: false,
                    Error: "person-approval-required: IConsole requires [PersonOnly] human approval proof.",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        PcResourceResponse response;
        try
        {
            response = await _localResourceExecutor!.ExecuteAsync(job, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = _thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Mode B local PC resource job {JobId} ({Capability}/{Operation}) failed with {ErrorType}.", job.JobId, job.Capability, job.Operation, error.GetType().Name);
            response = new PcResourceResponse(
                Ok: false,
                Error: error.Message,
                ExecutedByMachineId: _thisMachine.Value);
        }

        await _cloud.ReportResourceResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private async Task ExecuteEngineeringJobAsync(ConnectedEngineeringRequest job, CancellationToken ct)
    {
        // Fail-closed: strictly requires local AllowConnectedEngineering == true; NEVER falls back to AllowClientCodeSandbox.
        if (!EffectiveAllowConnectedEngineering())
        {
            _logger.LogInformation(
                "Refused Plane 2 connected engineering job {JobId} (run={RunId}): AllowConnectedEngineering is disabled on this PC.",
                job.JobId,
                job.RunId);
            await _cloud.ReportEngineeringResultAsync(
                job.JobId,
                new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: job.RunId,
                    Error: "consent-disabled: AllowConnectedEngineering is disabled on this PC.",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        var executionTimeout = TimeSpan.FromSeconds(Math.Clamp(job.TimeoutSeconds, 15, 900));
        using var timeoutCts = new CancellationTokenSource(executionTimeout, _clock);
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var jobCt = jobCts.Token;

        if (_localEngineeringExecutor is IStreamingLocalEngineeringExecutor streamingExecutor)
        {
            await ExecuteStreamingEngineeringJobAsync(job, streamingExecutor, jobCt, ct).ConfigureAwait(false);
            return;
        }

        ConnectedEngineeringResponse response;
        try
        {
            response = await _localEngineeringExecutor!.ExecuteAsync(job, jobCt).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = _thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Local Plane 2 engineering job {JobId} (run={RunId}) timed out after {TimeoutSeconds}s.", job.JobId, job.RunId, (int)executionTimeout.TotalSeconds);
            response = new ConnectedEngineeringResponse(
                Ok: false,
                RunId: job.RunId,
                Error: $"timeout: Local Plane 2 engineering job exceeded {(int)executionTimeout.TotalSeconds}s execution cap.",
                ExecutedByMachineId: _thisMachine.Value);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Local Plane 2 engineering job {JobId} (run={RunId}) failed with {ErrorType}.", job.JobId, job.RunId, error.GetType().Name);
            response = new ConnectedEngineeringResponse(
                Ok: false,
                RunId: job.RunId,
                Error: error.Message,
                ExecutedByMachineId: _thisMachine.Value);
        }

        await _cloud.ReportEngineeringResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private async Task ExecuteStreamingEngineeringJobAsync(
        ConnectedEngineeringRequest job,
        IStreamingLocalEngineeringExecutor streamingExecutor,
        CancellationToken jobCt,
        CancellationToken outerCt)
    {
        ConnectedEngineeringResponse? capturedResult = null;
        string? capturedError = null;
        var producerRan = false;

        using var content = new NdjsonPushContent<EngineeringStreamChunk>(
            DeviceWireJson.Default.EngineeringStreamChunk,
            async (write, token) =>
            {
                producerRan = true;
                var emittedDone = false;
                Exception? execError = null;
                IAsyncEnumerator<EngineeringStreamChunk>? enumerator = null;
                using var streamExecCts = CancellationTokenSource.CreateLinkedTokenSource(token, jobCt);
                var execToken = streamExecCts.Token;
                try
                {
                    try
                    {
                        enumerator = streamingExecutor.ExecuteStreamingAsync(job, execToken).GetAsyncEnumerator(execToken);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested || outerCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        execError = new TimeoutException($"Local Plane 2 engineering job exceeded {Math.Clamp(job.TimeoutSeconds, 15, 900)}s execution cap.");
                    }
                    catch (Exception error)
                    {
                        _logger.LogWarning(error, "Streaming Plane 2 engineering job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
                        execError = error;
                    }

                    while (execError is null && enumerator is not null)
                    {
                        EngineeringStreamChunk rawChunk;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            rawChunk = enumerator.Current;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested || outerCt.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            execError = new TimeoutException($"Local Plane 2 engineering job exceeded {Math.Clamp(job.TimeoutSeconds, 15, 900)}s execution cap.");
                            break;
                        }
                        catch (Exception error)
                        {
                            _logger.LogWarning(error, "Streaming Plane 2 engineering job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
                            execError = error;
                            break;
                        }

                        var chunk = StampEngineeringMachineId(rawChunk);
                        if (chunk.Result is not null)
                        {
                            capturedResult = chunk.Result;
                        }

                        if (!string.IsNullOrWhiteSpace(chunk.Error))
                        {
                            capturedError = chunk.Error;
                        }

                        if (chunk.Done)
                        {
                            emittedDone = true;
                        }

                        // Isolated from executor catch so transport write() failures propagate directly.
                        await write(chunk, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (execError is not null)
                {
                    capturedResult = new ConnectedEngineeringResponse(
                        Ok: false,
                        RunId: job.RunId,
                        Error: execError.Message,
                        ExecutedByMachineId: _thisMachine.Value);
                    await write(new EngineeringStreamChunk(Done: true, Error: execError.Message, Result: capturedResult), token).ConfigureAwait(false);
                }
                else if (!emittedDone)
                {
                    capturedResult ??= new ConnectedEngineeringResponse(
                        Ok: false,
                        RunId: job.RunId,
                        Error: capturedError ?? "Streaming engineering execution completed without a terminal result.",
                        ExecutedByMachineId: _thisMachine.Value);
                    await write(new EngineeringStreamChunk(Done: true, Result: capturedResult), token).ConfigureAwait(false);
                }
            });

        var streamed = await _cloud.StreamEngineeringAsync(job.JobId, content, outerCt).ConfigureAwait(false);
        if (streamed)
        {
            return;
        }

        // Backward compatibility: older relays return 404/501 for /engineering/{jobId}/stream.
        // Only execute via the unary path if the streaming producer never ran; never double-execute.
        ConnectedEngineeringResponse fallback;
        if (!producerRan)
        {
            try
            {
                fallback = await streamingExecutor.ExecuteAsync(job, jobCt).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(fallback.ExecutedByMachineId))
                {
                    fallback = fallback with { ExecutedByMachineId = _thisMachine.Value };
                }
            }
            catch (OperationCanceledException) when (outerCt.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                fallback = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: job.RunId,
                    Error: $"timeout: Local Plane 2 engineering job exceeded {Math.Clamp(job.TimeoutSeconds, 15, 900)}s execution cap.",
                    ExecutedByMachineId: _thisMachine.Value);
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Local Plane 2 engineering job {JobId} fallback failed with {ErrorType}.", job.JobId, error.GetType().Name);
                fallback = new ConnectedEngineeringResponse(
                    Ok: false,
                    RunId: job.RunId,
                    Error: error.Message,
                    ExecutedByMachineId: _thisMachine.Value);
            }
        }
        else
        {
            fallback = capturedResult ?? new ConnectedEngineeringResponse(
                Ok: false,
                RunId: job.RunId,
                Error: capturedError ?? "Streaming engineering execution completed without a terminal result.",
                ExecutedByMachineId: _thisMachine.Value);
        }

        await _cloud.ReportEngineeringResultAsync(job.JobId, fallback, outerCt).ConfigureAwait(false);
    }

    private EngineeringStreamChunk StampEngineeringMachineId(EngineeringStreamChunk chunk)
    {
        if (chunk.Result is null || !string.IsNullOrWhiteSpace(chunk.Result.ExecutedByMachineId))
        {
            return chunk;
        }

        return chunk with
        {
            Result = chunk.Result with { ExecutedByMachineId = _thisMachine.Value },
        };
    }

    private async Task ExecuteSandboxJobAsync(ConnectedSandboxRequest job, CancellationToken ct)
    {
        var compute = CurrentComputeOptions();
        var allowClientCodeSandbox = EffectiveAllowClientCodeSandbox(compute);
        if (!compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox))
        {
            _logger.LogInformation(
                "Refused connected sandbox job {JobId}: local compute consent mode is {ConsentMode} (AllowClientCodeSandbox={AllowClientCodeSandbox}).",
                job.JobId,
                compute.ConsentMode,
                allowClientCodeSandbox);
            await _cloud.ReportSandboxResultAsync(
                job.JobId,
                new ConnectedSandboxResponse(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: null,
                    Error: "consent-disabled",
                    ExecutedByMachineId: _thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (_localSandbox is IStreamingLocalSandboxExecutor streamingSandbox)
        {
            await ExecuteStreamingSandboxJobAsync(job, streamingSandbox, ct).ConfigureAwait(false);
            return;
        }

        ConnectedSandboxResponse response;
        try
        {
            response = await _localSandbox!.ExecuteAsync(job, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = _thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Local UnitHost sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
            response = new ConnectedSandboxResponse(
                Handled: false,
                ResultJson: null,
                UpdatedStateJson: null,
                Error: error.Message,
                ExecutedByMachineId: _thisMachine.Value);
        }

        await _cloud.ReportSandboxResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private async Task ExecuteStreamingSandboxJobAsync(
        ConnectedSandboxRequest job,
        IStreamingLocalSandboxExecutor streamingSandbox,
        CancellationToken ct)
    {
        ConnectedSandboxResponse? capturedResult = null;
        string? capturedError = null;
        var producerRan = false;

        using var content = new NdjsonPushContent<SandboxStreamChunk>(
            DeviceWireJson.Default.SandboxStreamChunk,
            async (write, token) =>
            {
                producerRan = true;
                var emittedDone = false;
                Exception? sandboxError = null;
                IAsyncEnumerator<SandboxStreamChunk>? enumerator = null;
                try
                {
                    try
                    {
                        enumerator = streamingSandbox.ExecuteStreamingAsync(job, token).GetAsyncEnumerator(token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        _logger.LogWarning(error, "Streaming local sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
                        sandboxError = error;
                    }

                    while (sandboxError is null && enumerator is not null)
                    {
                        SandboxStreamChunk rawChunk;
                        try
                        {
                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            rawChunk = enumerator.Current;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception error)
                        {
                            _logger.LogWarning(error, "Streaming local sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
                            sandboxError = error;
                            break;
                        }

                        var chunk = StampMachineId(rawChunk);
                        if (chunk.Result is not null)
                        {
                            capturedResult = chunk.Result;
                        }

                        if (!string.IsNullOrWhiteSpace(chunk.Error))
                        {
                            capturedError = chunk.Error;
                        }

                        if (chunk.Done)
                        {
                            emittedDone = true;
                        }

                        // Isolated from sandbox catch so transport write() failures propagate directly.
                        await write(chunk, token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (sandboxError is not null)
                {
                    capturedResult = new ConnectedSandboxResponse(
                        Handled: false,
                        ResultJson: null,
                        UpdatedStateJson: null,
                        Error: sandboxError.Message,
                        ExecutedByMachineId: _thisMachine.Value);
                    await write(new SandboxStreamChunk(Done: true, Error: sandboxError.Message, Result: capturedResult), token).ConfigureAwait(false);
                }
                else if (!emittedDone)
                {
                    capturedResult ??= new ConnectedSandboxResponse(
                        Handled: true,
                        ResultJson: null,
                        UpdatedStateJson: null,
                        Error: null,
                        ExecutedByMachineId: _thisMachine.Value);
                    await write(new SandboxStreamChunk(Done: true, Result: capturedResult), token).ConfigureAwait(false);
                }
            });

        var streamed = await _cloud.StreamSandboxAsync(job.JobId, content, ct).ConfigureAwait(false);
        if (streamed)
        {
            return;
        }

        // Backward compatibility: older relays return 404/501 for /sandbox/{jobId}/stream.
        // Only execute via the unary path if the streaming producer never ran; never double-execute.
        ConnectedSandboxResponse fallback;
        if (!producerRan)
        {
            try
            {
                fallback = await streamingSandbox.ExecuteAsync(job, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(fallback.ExecutedByMachineId))
                {
                    fallback = fallback with { ExecutedByMachineId = _thisMachine.Value };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Local UnitHost sandbox job {JobId} fallback failed with {ErrorType}.", job.JobId, error.GetType().Name);
                fallback = new ConnectedSandboxResponse(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: null,
                    Error: error.Message,
                    ExecutedByMachineId: _thisMachine.Value);
            }
        }
        else
        {
            fallback = capturedResult ?? new ConnectedSandboxResponse(
                Handled: false,
                ResultJson: null,
                UpdatedStateJson: null,
                Error: capturedError ?? "Streaming sandbox execution completed without a terminal result.",
                ExecutedByMachineId: _thisMachine.Value);
        }

        await _cloud.ReportSandboxResultAsync(job.JobId, fallback, ct).ConfigureAwait(false);
    }

    private SandboxStreamChunk StampMachineId(SandboxStreamChunk chunk)
    {
        if (chunk.Result is null || !string.IsNullOrWhiteSpace(chunk.Result.ExecutedByMachineId))
        {
            return chunk;
        }

        return chunk with
        {
            Result = chunk.Result with { ExecutedByMachineId = _thisMachine.Value },
        };
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

    private async Task ExecuteJobAsync(DeviceChatJob job, CancellationToken ct)
    {
        using var activity = StartJobActivity(job);

        if (!_advertised.TryGetValue(job.Model, out var target))
        {
            _logger.LogInformation("Refused device job {JobId} for model {Model}: not-ready.", job.JobId, job.Model);
            using var refused = new NdjsonPushContent<DeviceChunk>(
                DeviceWireJson.Default.DeviceChunk,
                async (write, token) =>
                {
                    await write(new DeviceChunk(Delta: null, Done: true, Error: "not-ready"), token).ConfigureAwait(false);
                });
            await _cloud.StreamAsync(job.JobId, refused, ct).ConfigureAwait(false);
            return;
        }

        var messages = new List<ChatMessage>(job.Messages.Length);
        foreach (var msg in job.Messages)
        {
            var role = msg.Role.ToLowerInvariant() switch
            {
                "system" => ChatRole.System,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.User,
            };
            messages.Add(new ChatMessage(role, msg.Text));
        }

        var chatOptions = new ChatOptions
        {
            ModelId = target.Card.Id.Value,
            Temperature = job.Temperature,
            MaxOutputTokens = job.MaxOutputTokens,
            Tools = null,
            ToolMode = ChatToolMode.None,
        };

        int? inputTokens = null;
        int? outputTokens = null;

        using var client = target.Runtime.Chat(target.Card.Id);
        using var content = new NdjsonPushContent<DeviceChunk>(
            DeviceWireJson.Default.DeviceChunk,
            async (write, token) =>
            {
                try
                {
                    await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, token).ConfigureAwait(false))
                    {
                        foreach (var usage in update.Contents.OfType<UsageContent>())
                        {
                            if (usage.Details.InputTokenCount is { } input)
                            {
                                inputTokens = (inputTokens ?? 0) + (int)Math.Min(input, int.MaxValue);
                            }
                            if (usage.Details.OutputTokenCount is { } output)
                            {
                                outputTokens = (outputTokens ?? 0) + (int)Math.Min(output, int.MaxValue);
                            }
                        }

                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            await write(new DeviceChunk(update.Text), token).ConfigureAwait(false);
                        }
                    }

                    await write(new DeviceChunk(null, Done: true, Error: null, InputTokens: inputTokens, OutputTokens: outputTokens), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    _logger.LogWarning("Device job {JobId} on model {Model} failed with {ErrorType}.", job.JobId, job.Model, error.GetType().Name);
                    activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                    await write(new DeviceChunk(null, Done: true, Error: error.Message, InputTokens: inputTokens, OutputTokens: outputTokens), token).ConfigureAwait(false);
                }
            });

        await _cloud.StreamAsync(job.JobId, content, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Completed device job {JobId} on model {Model} (input_tokens={InputTokens}, output_tokens={OutputTokens}).",
            job.JobId,
            job.Model,
            inputTokens ?? 0,
            outputTokens ?? 0);
    }

    private static Activity? StartJobActivity(DeviceChatJob job)
    {
        Activity? activity;
        if (!string.IsNullOrWhiteSpace(job.TraceParent)
            && ActivityContext.TryParse(job.TraceParent, traceState: null, isRemote: true, out var parentContext))
        {
            activity = Telemetry.StartActivity("devices.worker.job", ActivityKind.Consumer, parentContext);
            if (activity is null)
            {
                activity = new Activity("devices.worker.job");
                activity.SetParentId(parentContext.TraceId, parentContext.SpanId, parentContext.TraceFlags);
                activity.Start();
            }
        }
        else
        {
            activity = Telemetry.StartActivity("devices.worker.job", ActivityKind.Consumer);
        }

        activity?.SetTag("job.id", job.JobId);
        activity?.SetTag("gen_ai.request.model", job.Model);
        return activity;
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

    private async Task ResetAdvertisedAsync(CancellationToken ct)
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

    private void OnSandboxConsentChanged(bool _) => ReleaseSignal(_heartbeatWake);

    private void OnResourceConsentChanged() => ReleaseSignal(_heartbeatWake);

    private void OnEngineeringConsentChanged(bool _)
    {
        ReleaseSignal(_heartbeatWake);
        ReleaseSignal(_engineeringConsentWake);
    }

    private void ReleaseSignal(SemaphoreSlim signal)
    {
        if (signal.CurrentCount == 0)
        {
            try { signal.Release(); }
            catch (SemaphoreFullException ex)
            {
                _logger.LogDebug(ex, "DeviceWorker wake signal already queued.");
            }
        }
    }

    private ComputeCapabilityOptions CurrentComputeOptions()
        => _options?.CurrentValue.Compute ?? new ComputeCapabilityOptions();

    private bool EffectiveAllowClientCodeSandbox(ComputeCapabilityOptions compute)
        => _consentStore is not null
            ? _consentStore.Resolve(compute.AllowClientCodeSandbox)
            : compute.AllowClientCodeSandbox;

    private bool EffectiveAllowWindowsFolderGateway()
        => _resourceConsent?.AllowWindowsFolderGateway ?? false;

    private bool EffectiveAllowConsole()
        => _resourceConsent?.AllowConsole ?? false;

    private bool EffectiveAllowConnectedEngineering()
        => _engineeringConsent?.AllowConnectedEngineering ?? false;

    private async Task DelayOrWakeAsync(SemaphoreSlim signal, TimeSpan delay, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, _clock, delayCts.Token);
        var wakeTask = signal.WaitAsync(delayCts.Token);
        var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(delayTask, wakeTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        if (completed == wakeTask && ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
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

    private async Task PersistRevokedStatusBestEffortAsync()
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            var brainId = IntoChatOSNames.DefaultOwnerBrainId;
            var edge = _services.GetService<EdgeAuthority>();
            using var scope = edge?.Enter(edge.Module(DevicesModule.ManifestName, new BrainId(brainId)));
            var link = SiloNeurons.Get<IDeviceLink>(_services, IDeviceLink.Key(brainId));
            _ = await link.Status().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _logger.LogDebug(error, "Could not persist revoked device link status immediately; status will reconcile on next check.");
        }
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

    private async Task DelayOrWakeQuietlyAsync(SemaphoreSlim signal, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await DelayOrWakeAsync(signal, delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task DelayQuietlyAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
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

    private sealed class NdjsonPushContent<T> : HttpContent
    {
        private readonly JsonTypeInfo<T> _typeInfo;
        private readonly Func<Func<T, CancellationToken, Task>, CancellationToken, Task> _producer;

        public NdjsonPushContent(
            JsonTypeInfo<T> typeInfo,
            Func<Func<T, CancellationToken, Task>, CancellationToken, Task> producer)
        {
            _typeInfo = typeInfo;
            _producer = producer;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson") { CharSet = "utf-8" };
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
            await _producer(async (chunk, ct) =>
            {
                var line = JsonSerializer.Serialize(chunk, _typeInfo);
                await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await writer.WriteAsync('\n').ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
