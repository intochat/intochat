using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
// long-polls GET /circle/v1/compute/next, executes text-only chat jobs on the local ILocalRuntime,
// and executes scoped Plane 1 sandbox jobs on the local IntoChat.OS.Sandbox (--unit-host).
internal sealed class DeviceWorker(
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
    ILocalPcResourceExecutor? localResourceExecutor = null) : BackgroundService
{
    private static readonly ActivitySource Telemetry = new("IntoChat.OS.Modules.Devices");
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILocalRuntime[] _runtimes = OrderRuntimes(runtimes);
    private readonly Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)> _advertised = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private volatile bool _modelsDirty = true;
    private bool _reportedOnce;
    private bool _computeReportedOnce;
    private bool _lastAdvertisedSandboxReady;
    private bool _lastAdvertisedAllowClientCodeSandbox;
    private bool _lastAdvertisedAllowFolders;
    private bool _lastAdvertisedAllowConsole;
    private ComputeConsentMode _lastAdvertisedConsentMode = ComputeConsentMode.ExplicitToggle;
    private DateTimeOffset _lastComputeHeartbeatAt = DateTimeOffset.MinValue;

    internal void NotifyLinkChanged()
    {
        _modelsDirty = true;
        _computeReportedOnce = false;
        if (_wakeSignal.CurrentCount == 0)
        {
            try { _wakeSignal.Release(); }
            catch (SemaphoreFullException ex)
            {
                logger.LogDebug(ex, "DeviceWorker wake signal already queued.");
            }
        }
    }

    internal void NotifyInstallChanged(InstallChanged signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.State is InstallState.Ready or InstallState.Cancelled or InstallState.Failed)
        {
            _modelsDirty = true;
        }
    }

    internal void NotifyModelsChanged() => _modelsDirty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await credentials.Read(stoppingToken).ConfigureAwait(false) is null)
                {
                    ResetAdvertised();
                    await DelayOrWakeAsync(IdleDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await Tick(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (DeviceLinkRevokedException error)
            {
                logger.LogInformation(error, "Device worker stopping poll: cloud credential was revoked.");
                ResetAdvertised();
                await PersistRevokedStatusBestEffortAsync().ConfigureAwait(false);
                await DelayQuietlyAsync(ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                logger.LogWarning(error, "Device worker could not reach cloud; retrying.");
                _modelsDirty = true;
                await DelayQuietlyAsync(ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Device worker cloud call timed out; retrying.");
                _modelsDirty = true;
                await DelayQuietlyAsync(ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Device worker encountered an unexpected error; retrying.");
                _modelsDirty = true;
                await DelayQuietlyAsync(ErrorDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    // One deterministic cycle: ensure Ready models and compute capability heartbeat are reported,
    // poll one chat job (and scoped sandbox job if a local sandbox executor is attached), and report answers.
    internal async Task Tick(CancellationToken ct = default)
    {
        var secrets = await credentials.Read(ct).ConfigureAwait(false);
        if (secrets is null)
        {
            ResetAdvertised();
            return;
        }

        await ReportReadyModelsIfNeededAsync(ct).ConfigureAwait(false);
        await ReportComputeCapabilityIfNeededAsync(secrets, ct).ConfigureAwait(false);

        if (localSandbox is not null)
        {
            var sandboxJob = await cloud.PollNextSandboxAsync(ct).ConfigureAwait(false);
            if (sandboxJob is not null)
            {
                await ExecuteSandboxJobAsync(sandboxJob, ct).ConfigureAwait(false);
            }
        }

        if (localResourceExecutor is not null)
        {
            var resourceJob = await cloud.PollNextResourceAsync(ct).ConfigureAwait(false);
            if (resourceJob is not null)
            {
                await ExecuteResourceJobAsync(resourceJob, ct).ConfigureAwait(false);
            }
        }

        var job = await cloud.PollNextAsync(ct).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        await ExecuteJobAsync(job, ct).ConfigureAwait(false);
    }

    internal ComputeCapabilityAdvertisement BuildComputeCapabilityAdvertisement(LinkSecrets secrets, string[]? consentedFolderLabels = null)
    {
        var compute = CurrentComputeOptions();
        var allowClientCodeSandbox = EffectiveAllowClientCodeSandbox(compute);
        var sandboxReady = compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox);
        var allowFolders = EffectiveAllowWindowsFolderGateway();
        var allowConsole = EffectiveAllowConsole();
        var safeFolderLabels = allowFolders ? consentedFolderLabels : null;
        return new ComputeCapabilityAdvertisement(
            MachineId: thisMachine.Value,
            DeviceName: DeviceName(),
            ConsentMode: compute.ConsentMode,
            SandboxReady: sandboxReady,
            SandboxRuntime: "unit-host",
            OsDescription: RuntimeInformation.OSDescription,
            MaxConcurrency: compute.MaxConcurrency,
            HeartbeatAt: _clock.GetUtcNow(),
            KeyThumbprint: ComputeKeyThumbprint(secrets.PrivateKeyPkcs8),
            AllowClientCodeSandbox: allowClientCodeSandbox,
            AdvertisedCapabilities: ClientComputeConsentPolicy.CapabilitiesFor(sandboxReady, allowFolders, allowConsole),
            AllowWindowsFolderGateway: allowFolders,
            AllowConsole: allowConsole,
            ConsentedFolderLabels: safeFolderLabels,
            ConsoleRequiresPersonApproval: true);
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
        var anyConsented = sandboxReady || allowFolders || allowConsole;

        var stateChanged = !_computeReportedOnce
            || _lastAdvertisedSandboxReady != sandboxReady
            || _lastAdvertisedConsentMode != compute.ConsentMode
            || _lastAdvertisedAllowClientCodeSandbox != allowClientCodeSandbox
            || _lastAdvertisedAllowFolders != allowFolders
            || _lastAdvertisedAllowConsole != allowConsole;
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
        if (allowFolders && localResourceExecutor is not null)
        {
            try
            {
                folderLabels = await localResourceExecutor.GetConsentedFolderLabelsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogDebug(error, "Could not enumerate consented folder labels for capability heartbeat.");
            }
        }

        var advertisement = BuildComputeCapabilityAdvertisement(secrets, folderLabels);
        await cloud.ReportComputeCapabilityAsync(advertisement, ct).ConfigureAwait(false);

        if (computeRouter is not null)
        {
            var ownerBrain = new BrainId(IntoChatOSNames.DefaultOwnerBrainId);
            await computeRouter.RecordHeartbeatAsync(ownerBrain, advertisement, ct).ConfigureAwait(false);
            if (!anyConsented)
            {
                await computeRouter.MarkOfflineAsync(ownerBrain, thisMachine.Value, ct).ConfigureAwait(false);
            }
        }

        _computeReportedOnce = true;
        _lastAdvertisedSandboxReady = sandboxReady;
        _lastAdvertisedAllowClientCodeSandbox = allowClientCodeSandbox;
        _lastAdvertisedAllowFolders = allowFolders;
        _lastAdvertisedAllowConsole = allowConsole;
        _lastAdvertisedConsentMode = compute.ConsentMode;
        _lastComputeHeartbeatAt = now;
    }

    private async Task ExecuteResourceJobAsync(ConnectedResourceRequest job, CancellationToken ct)
    {
        var isConsole = job.Kind == ConnectedResourceKind.ConsoleRun;
        var allowFolders = EffectiveAllowWindowsFolderGateway();
        var allowConsole = EffectiveAllowConsole();

        if (!isConsole && !allowFolders)
        {
            logger.LogInformation("Refused Mode B folder job {JobId}: IWindowsFolderGateway consent is OFF.", job.JobId);
            await cloud.ReportResourceResultAsync(
                job.JobId,
                new ConnectedResourceResponse(
                    Handled: false,
                    Error: "consent-disabled: IWindowsFolderGateway is disabled on this PC.",
                    ExecutedByMachineId: thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && !allowConsole)
        {
            logger.LogInformation("Refused Mode B console job {JobId}: IConsole consent is OFF.", job.JobId);
            await cloud.ReportResourceResultAsync(
                job.JobId,
                new ConnectedResourceResponse(
                    Handled: false,
                    Error: "consent-disabled: IConsole is disabled on this PC.",
                    ExecutedByMachineId: thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        if (isConsole && (string.IsNullOrWhiteSpace(job.ApprovalPlanId) || string.IsNullOrWhiteSpace(job.ApprovalProof)))
        {
            logger.LogInformation("Refused Mode B console job {JobId}: missing [PersonOnly] approval proof.", job.JobId);
            await cloud.ReportResourceResultAsync(
                job.JobId,
                new ConnectedResourceResponse(
                    Handled: false,
                    Error: "person-approval-required: IConsole requires [PersonOnly] human approval proof.",
                    ExecutedByMachineId: thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        ConnectedResourceResponse response;
        try
        {
            response = await localResourceExecutor!.ExecuteAsync(job, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Mode B local PC resource job {JobId} ({Kind}) failed with {ErrorType}.", job.JobId, job.Kind, error.GetType().Name);
            response = new ConnectedResourceResponse(
                Handled: false,
                Error: error.Message,
                ExecutedByMachineId: thisMachine.Value);
        }

        await cloud.ReportResourceResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private async Task ExecuteSandboxJobAsync(ConnectedSandboxRequest job, CancellationToken ct)
    {
        var compute = CurrentComputeOptions();
        var allowClientCodeSandbox = EffectiveAllowClientCodeSandbox(compute);
        if (!compute.IsClientCodeSandboxConsentedWith(allowClientCodeSandbox))
        {
            logger.LogInformation(
                "Refused connected sandbox job {JobId}: local compute consent mode is {ConsentMode} (AllowClientCodeSandbox={AllowClientCodeSandbox}).",
                job.JobId,
                compute.ConsentMode,
                allowClientCodeSandbox);
            await cloud.ReportSandboxResultAsync(
                job.JobId,
                new ConnectedSandboxResponse(
                    Handled: false,
                    ResultJson: null,
                    UpdatedStateJson: null,
                    Error: "consent-disabled",
                    ExecutedByMachineId: thisMachine.Value),
                ct).ConfigureAwait(false);
            return;
        }

        ConnectedSandboxResponse response;
        try
        {
            response = await localSandbox!.ExecuteAsync(job, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ExecutedByMachineId))
            {
                response = response with { ExecutedByMachineId = thisMachine.Value };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Local UnitHost sandbox job {JobId} failed with {ErrorType}.", job.JobId, error.GetType().Name);
            response = new ConnectedSandboxResponse(
                Handled: false,
                ResultJson: null,
                UpdatedStateJson: null,
                Error: error.Message,
                ExecutedByMachineId: thisMachine.Value);
        }

        await cloud.ReportSandboxResultAsync(job.JobId, response, ct).ConfigureAwait(false);
    }

    private async Task ReportReadyModelsIfNeededAsync(CancellationToken ct)
    {
        var ready = await CollectReadyModelsAsync(ct).ConfigureAwait(false);
        var changed = !_reportedOnce || _modelsDirty || !SameModelKeys(ready, _advertised);
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

        await cloud.ReportModelsAsync(new ModelsReport(DeviceName(), advertised), ct).ConfigureAwait(false);

        _advertised.Clear();
        foreach (var (id, value) in ready)
        {
            _advertised[id] = value;
        }

        _reportedOnce = true;
        _modelsDirty = false;
    }

    private async Task ExecuteJobAsync(DeviceChatJob job, CancellationToken ct)
    {
        using var activity = StartJobActivity(job);

        if (!_advertised.TryGetValue(job.Model, out var target))
        {
            logger.LogInformation("Refused device job {JobId} for model {Model}: not-ready.", job.JobId, job.Model);
            using var refused = new NdjsonPushContent(async (write, token) =>
            {
                await write(new DeviceChunk(Delta: null, Done: true, Error: "not-ready"), token).ConfigureAwait(false);
            });
            await cloud.StreamAsync(job.JobId, refused, ct).ConfigureAwait(false);
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

        var options = new ChatOptions
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
        using var content = new NdjsonPushContent(async (write, token) =>
        {
            try
            {
                await foreach (var update in client.GetStreamingResponseAsync(messages, options, token).ConfigureAwait(false))
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
                logger.LogWarning("Device job {JobId} on model {Model} failed with {ErrorType}.", job.JobId, job.Model, error.GetType().Name);
                activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                await write(new DeviceChunk(null, Done: true, Error: error.Message, InputTokens: inputTokens, OutputTokens: outputTokens), token).ConfigureAwait(false);
            }
        });

        await cloud.StreamAsync(job.JobId, content, ct).ConfigureAwait(false);
        logger.LogInformation(
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
                logger.LogDebug(error, "Skipping unreachable local runtime {RuntimeId}.", runtime.Id.Value);
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
                logger.LogDebug(error, "Could not list installed models on runtime {RuntimeId}.", runtime.Id.Value);
            }
        }
        return ready;
    }

    private void ResetAdvertised()
    {
        _advertised.Clear();
        _reportedOnce = false;
        _modelsDirty = true;
        _computeReportedOnce = false;
        _lastAdvertisedSandboxReady = false;
        _lastAdvertisedAllowClientCodeSandbox = false;
        _lastAdvertisedAllowFolders = false;
        _lastAdvertisedAllowConsole = false;
        _lastComputeHeartbeatAt = DateTimeOffset.MinValue;
    }

    private ComputeCapabilityOptions CurrentComputeOptions()
        => options?.CurrentValue.Compute ?? new ComputeCapabilityOptions();

    private bool EffectiveAllowClientCodeSandbox(ComputeCapabilityOptions compute)
        => consentStore is not null
            ? consentStore.Resolve(compute.AllowClientCodeSandbox)
            : compute.AllowClientCodeSandbox;

    private bool EffectiveAllowWindowsFolderGateway()
        => resourceConsent?.AllowWindowsFolderGateway ?? false;

    private bool EffectiveAllowConsole()
        => resourceConsent?.AllowConsole ?? false;

    private async Task DelayOrWakeAsync(TimeSpan delay, CancellationToken ct)
    {
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, _clock, delayCts.Token);
        var wakeTask = _wakeSignal.WaitAsync(delayCts.Token);
        var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
        if (completed == wakeTask)
        {
            await wakeTask.ConfigureAwait(false);
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
        if (services is null)
        {
            return;
        }

        try
        {
            var brainId = IntoChatOSNames.DefaultOwnerBrainId;
            var edge = services.GetService<EdgeAuthority>();
            using var scope = edge?.Enter(edge.Module(DevicesModule.ManifestName, new BrainId(brainId)));
            var link = SiloNeurons.Get<IDeviceLink>(services, IDeviceLink.Key(brainId));
            _ = await link.Status().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogDebug(error, "Could not persist revoked device link status immediately; status will reconcile on next check.");
        }
    }

    private string DeviceName()
    {
        var custom = deviceNameProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom.Trim();
        }

        return string.IsNullOrWhiteSpace(Environment.MachineName) ? thisMachine.Value : Environment.MachineName;
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
        Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)> a,
        Dictionary<string, (ILocalRuntime Runtime, ModelCard Card)> b)
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

    private sealed class NdjsonPushContent : HttpContent
    {
        private readonly Func<Func<DeviceChunk, CancellationToken, Task>, CancellationToken, Task> _producer;

        public NdjsonPushContent(Func<Func<DeviceChunk, CancellationToken, Task>, CancellationToken, Task> producer)
        {
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
                var line = JsonSerializer.Serialize(chunk, DeviceWireJson.Default.DeviceChunk);
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
