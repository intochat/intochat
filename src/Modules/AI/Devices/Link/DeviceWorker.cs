using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Link;

// Background service on the PC: coordinates link lifecycle, heartbeats, consent wakes, and independent
// concurrent long-poll lanes for chat, Plane 1 sandbox, Mode B PC resource, and Plane 2 engineering jobs
// so a 25 s idle poll or transient error on one lane never starves another.
internal sealed class DeviceWorker : BackgroundService
{
    internal static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan InitialBackoffDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxBackoffDelay = TimeSpan.FromSeconds(30);

    private readonly CloudClient _cloud;
    private readonly DeviceCredentials _credentials;
    private readonly ILogger<DeviceWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly IServiceProvider? _services;
    private readonly IClientCodeSandboxConsent? _consentStore;
    private readonly IClientResourceConsent? _resourceConsent;
    private readonly IClientEngineeringConsent? _engineeringConsent;
    private readonly Func<double> _jitterProvider;

    private readonly DeviceCapabilityReporter _capabilities;
    private readonly DeviceChatJobExecutor _chatExecutor;
    private readonly DeviceSandboxJobExecutor? _sandboxExecutor;
    private readonly DeviceResourceJobExecutor? _resourceExecutor;
    private readonly DeviceEngineeringJobExecutor? _engineeringExecutor;

    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _heartbeatWake = new(0, 1);
    private readonly SemaphoreSlim _engineeringConsentWake = new(0, 1);

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
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _services = services;
        _consentStore = consentStore;
        _resourceConsent = resourceConsent;
        _engineeringConsent = engineeringConsent;
        _jitterProvider = jitterProvider ?? DefaultJitter;

        _capabilities = new DeviceCapabilityReporter(
            cloud,
            runtimes,
            thisMachine,
            logger,
            _clock,
            deviceNameProvider,
            options,
            computeRouter,
            consentStore,
            resourceConsent,
            localResourceExecutor,
            engineeringConsent);

        _chatExecutor = new DeviceChatJobExecutor(cloud, _capabilities, logger);
        _sandboxExecutor = localSandbox is not null
            ? new DeviceSandboxJobExecutor(cloud, localSandbox, _capabilities, thisMachine, logger)
            : null;
        _resourceExecutor = localResourceExecutor is not null
            ? new DeviceResourceJobExecutor(cloud, localResourceExecutor, _capabilities, thisMachine, logger)
            : null;
        _engineeringExecutor = localEngineeringExecutor is not null
            ? new DeviceEngineeringJobExecutor(cloud, localEngineeringExecutor, _capabilities, thisMachine, _clock, logger)
            : null;

        _consentStore?.Changed += OnSandboxConsentChanged;
        _resourceConsent?.Changed += OnResourceConsentChanged;
        _engineeringConsent?.Changed += OnEngineeringConsentChanged;
    }

    internal void NotifyLinkChanged()
    {
        _capabilities.MarkLinkChanged();
        ReleaseSignal(_wakeSignal);
        ReleaseSignal(_heartbeatWake);
        ReleaseSignal(_engineeringConsentWake);
    }

    internal void NotifyInstallChanged(InstallChanged signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.State is InstallState.Ready or InstallState.Cancelled or InstallState.Failed)
        {
            _capabilities.MarkModelsDirty();
            ReleaseSignal(_heartbeatWake);
        }
    }

    internal void NotifyModelsChanged()
    {
        _capabilities.MarkModelsDirty();
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
                    await _capabilities.ResetAsync(stoppingToken).ConfigureAwait(false);
                    sessionFailures = 0;
                    await DelayOrWakeAsync(_wakeSignal, IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await _capabilities.SyncMetadataAsync(secrets, stoppingToken).ConfigureAwait(false);
                sessionFailures = 0;

                var revoked = await RunLinkedSessionAsync(stoppingToken).ConfigureAwait(false);
                if (revoked)
                {
                    await _capabilities.ResetAsync(CancellationToken.None).ConfigureAwait(false);
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
                await _capabilities.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await PersistRevokedStatusBestEffortAsync().ConfigureAwait(false);
                sessionFailures = 0;
                await DelayOrWakeAsync(_wakeSignal, ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                sessionFailures++;
                var delay = ComputeBackoffDelay(sessionFailures, _jitterProvider);
                var reason = error switch
                {
                    HttpRequestException => "could not reach cloud",
                    TaskCanceledException => "cloud call timed out",
                    _ => "encountered an unexpected error",
                };
                _logger.LogWarning(error, "Device worker {Reason}; retrying in {DelayMs:F0} ms.", reason, delay.TotalMilliseconds);
                _capabilities.MarkModelsDirty();
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
            await _capabilities.ResetAsync(ct).ConfigureAwait(false);
            return;
        }

        await _capabilities.SyncMetadataAsync(secrets, ct).ConfigureAwait(false);

        var laneTasks = new List<Task>(4)
        {
            PollAndExecuteChatOnceAsync(ct),
        };

        if (_sandboxExecutor is not null)
        {
            laneTasks.Add(PollAndExecuteSandboxOnceAsync(ct));
        }

        if (_resourceExecutor is not null)
        {
            laneTasks.Add(PollAndExecuteResourceOnceAsync(ct));
        }

        if (_engineeringExecutor is not null)
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

    internal ComputeCapabilityAdvertisement BuildComputeCapabilityAdvertisement(LinkSecrets secrets, string[]? consentedFolderLabels = null)
        => _capabilities.BuildComputeCapabilityAdvertisement(secrets, consentedFolderLabels);

    private static double DefaultJitter() => Random.Shared.NextDouble();

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

        void SignalUnlinked() => sessionCts.Cancel();

        var supervisedTasks = new List<Task>(5)
        {
            RunHeartbeatLoopAsync(sessionCt, SignalRevoked, SignalUnlinked),
            RunLaneLoopAsync("chat", PollAndExecuteChatOnceAsync, sessionCt, SignalRevoked),
        };

        if (_sandboxExecutor is not null)
        {
            supervisedTasks.Add(RunLaneLoopAsync("sandbox", PollAndExecuteSandboxOnceAsync, sessionCt, SignalRevoked));
        }

        if (_resourceExecutor is not null)
        {
            supervisedTasks.Add(RunLaneLoopAsync("resource", PollAndExecuteResourceOnceAsync, sessionCt, SignalRevoked));
        }

        if (_engineeringExecutor is not null)
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
            var interval = TimeSpan.FromSeconds(Math.Clamp(_capabilities.CurrentComputeOptions().HeartbeatIntervalSeconds, 1, 300));
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

                await _capabilities.SyncMetadataAsync(secrets, sessionCt).ConfigureAwait(false);
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
                _capabilities.MarkModelsDirty();
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
            catch (Exception error)
            {
                consecutiveFailures++;
                var backoff = ComputeBackoffDelay(consecutiveFailures, _jitterProvider);
                var reason = error switch
                {
                    HttpRequestException => "could not reach cloud",
                    TaskCanceledException => "cloud call timed out",
                    _ => "encountered an unexpected error",
                };
                _logger.LogWarning(
                    error,
                    "Device worker lane '{Lane}' {Reason} (attempt {Attempt}); backing off {DelayMs:F0} ms.",
                    laneName,
                    reason,
                    consecutiveFailures,
                    backoff.TotalMilliseconds);
                await DelayQuietlyAsync(backoff, sessionCt).ConfigureAwait(false);
            }
        }
    }

    private Task PollAndExecuteChatOnceAsync(CancellationToken ct)
        => PollAndExecuteLaneOnceAsync(_cloud.PollNextAsync, _chatExecutor.ExecuteAsync, ct);

    private Task PollAndExecuteSandboxOnceAsync(CancellationToken ct)
        => PollAndExecuteLaneOnceAsync(_cloud.PollNextSandboxAsync, _sandboxExecutor!.ExecuteAsync, ct);

    private Task PollAndExecuteResourceOnceAsync(CancellationToken ct)
        => PollAndExecuteLaneOnceAsync(_cloud.PollNextResourceAsync, _resourceExecutor!.ExecuteAsync, ct);

    private async Task PollAndExecuteEngineeringWhenConsentedOnceAsync(CancellationToken ct)
    {
        if (!_capabilities.EffectiveAllowConnectedEngineering())
        {
            await _engineeringConsentWake.WaitAsync(ct).ConfigureAwait(false);
            return;
        }

        await PollAndExecuteEngineeringOnceAsync(ct).ConfigureAwait(false);
    }

    private Task PollAndExecuteEngineeringOnceAsync(CancellationToken ct)
        => PollAndExecuteLaneOnceAsync(_cloud.PollNextEngineeringAsync, _engineeringExecutor!.ExecuteAsync, ct);

    private static async Task PollAndExecuteLaneOnceAsync<TJob>(
        Func<CancellationToken, Task<TJob?>> poll,
        Func<TJob, CancellationToken, Task> execute,
        CancellationToken ct)
        where TJob : class
    {
        var job = await poll(ct).ConfigureAwait(false);
        if (job is not null)
        {
            await execute(job, ct).ConfigureAwait(false);
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
}
