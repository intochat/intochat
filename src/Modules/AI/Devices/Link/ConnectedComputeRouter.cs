using System.Collections.Concurrent;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Authenticated P-256 relay transport for probing and dispatching Plane 1 scoped sandbox execution
/// to an owner's connected auto-all local client.
/// </summary>
internal sealed class RelayConnectedComputeTransport(
    CloudClient cloud,
    DeviceCredentials credentials,
    IOptionsMonitor<DevicesOptions>? options = null) : IConnectedComputeTransport
{
    public async Task<bool> ProbeAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (await credentials.Read(ct).ConfigureAwait(false) is null)
        {
            return false;
        }

        var response = await cloud.BrainComputeNodesAsync(brainId.Value, ct).ConfigureAwait(false);
        if (response is null || !string.Equals(response.Brain, brainId.Value, StringComparison.Ordinal))
        {
            return false;
        }

        return response.Nodes.Any(advertised =>
            string.Equals(advertised.MachineId, node.MachineId, StringComparison.Ordinal)
            && ClientComputeConsentPolicy.IsEligibleForCodeSandbox(
                advertised.ConsentMode,
                advertised.AllowClientCodeSandbox,
                advertised.SandboxReady,
                advertised.AdvertisedCapabilities));
    }

    public async Task<ConnectedSandboxResponse?> ExecuteAsync(
        BrainId brainId,
        ConnectedComputeNode node,
        ConnectedSandboxRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(request);
        if (await credentials.Read(ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var timeoutMs = Math.Clamp(options?.CurrentValue.Compute.AvailabilityTimeoutMs ?? 400, 50, 500);
        return await cloud.DispatchBrainSandboxAsync(
            new BrainSandboxDispatchRequest(brainId.Value, node.MachineId, request),
            TimeSpan.FromMilliseconds(timeoutMs),
            ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Tracks per-brain connected compute capability advertisements and heartbeats, and routes
/// Plane 1 code-orchestration sandbox execution to an owner's consented connected <c>ICodeSandbox</c>
/// local client with fast (&lt;500 ms) unavailability/disconnect detection and transparent fallback.
/// </summary>
internal sealed class ConnectedComputeRouter(
    CloudClient? cloud = null,
    DeviceCredentials? credentials = null,
    MachineId? thisMachine = null,
    IOptionsMonitor<DevicesOptions>? options = null,
    TimeProvider? clock = null,
    IConnectedComputeTransport? transport = null,
    ILogger<ConnectedComputeRouter>? logger = null) : IConnectedComputeRouter
{
    internal static readonly TimeSpan RelayCacheFor = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackedComputeNode>> _nodesByBrain = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _relayRefreshedAt = new(StringComparer.Ordinal);

    internal TimeSpan HeartbeatTtl
    {
        get
        {
            var seconds = options?.CurrentValue.Compute.HeartbeatTtlSeconds ?? (int)IConnectedComputeRouter.DefaultHeartbeatTtl.TotalSeconds;
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600));
        }
    }

    internal TimeSpan AvailabilityTimeout
    {
        get
        {
            var ms = options?.CurrentValue.Compute.AvailabilityTimeoutMs ?? (int)IConnectedComputeRouter.DefaultAvailabilityTimeout.TotalMilliseconds;
            return TimeSpan.FromMilliseconds(Math.Clamp(ms, 50, 500));
        }
    }

    public Task RecordHeartbeatAsync(
        BrainId brainId,
        ComputeCapabilityAdvertisement advertisement,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(advertisement);
        ArgumentException.ThrowIfNullOrWhiteSpace(advertisement.MachineId);
        ct.ThrowIfCancellationRequested();

        var now = _clock.GetUtcNow();
        var brainNodes = _nodesByBrain.GetOrAdd(
            brainId.Value,
            static _ => new ConcurrentDictionary<string, TrackedComputeNode>(StringComparer.Ordinal));

        var deviceName = string.IsNullOrWhiteSpace(advertisement.DeviceName)
            ? advertisement.MachineId
            : advertisement.DeviceName.Trim();
        var runtime = string.IsNullOrWhiteSpace(advertisement.SandboxRuntime)
            ? "unit-host"
            : advertisement.SandboxRuntime.Trim();
        var capabilities = advertisement.AdvertisedCapabilities?
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var tracked = new TrackedComputeNode(
            BrainId: brainId.Value,
            MachineId: advertisement.MachineId.Trim(),
            DeviceName: deviceName,
            ConsentMode: advertisement.ConsentMode,
            SandboxReady: advertisement.SandboxReady,
            SandboxRuntime: runtime,
            LastHeartbeatAt: now,
            KeyThumbprint: advertisement.KeyThumbprint,
            AllowClientCodeSandbox: advertisement.AllowClientCodeSandbox,
            AdvertisedCapabilities: capabilities,
            ExplicitlyOffline: false);

        brainNodes[tracked.MachineId] = tracked;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verifies a P-256 <see cref="MachineProof"/> signature and key thumbprint binding before accepting a
    /// compute capability advertisement and heartbeat over the paired device channel.
    /// </summary>
    public Task<ProofResult> RecordAuthenticatedHeartbeatAsync(
        BrainId brainId,
        ReadOnlySpan<byte> machinePublicKeySpki,
        string machineProof,
        string httpMethod,
        Uri requestUrl,
        string accessToken,
        INonceCache nonceCache,
        ComputeCapabilityAdvertisement advertisement,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(advertisement);

        var proofResult = MachineProof.Verify(
            machinePublicKeySpki,
            machineProof,
            httpMethod,
            requestUrl,
            accessToken,
            _clock.GetUtcNow(),
            nonceCache);

        if (!proofResult.IsValid)
        {
            return Task.FromResult(proofResult);
        }

        var expectedThumbprint = DeviceKeys.Thumbprint(machinePublicKeySpki);
        if (!string.IsNullOrWhiteSpace(advertisement.KeyThumbprint)
            && !string.Equals(advertisement.KeyThumbprint, expectedThumbprint, StringComparison.Ordinal))
        {
            return Task.FromResult(new ProofResult(ProofError.Binding));
        }

        var boundAdvertisement = advertisement with { KeyThumbprint = expectedThumbprint };
        return RecordAndReturnValidAsync(brainId, boundAdvertisement, proofResult, ct);
    }

    public Task MarkOfflineAsync(
        BrainId brainId,
        string machineId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        ct.ThrowIfCancellationRequested();

        if (_nodesByBrain.TryGetValue(brainId.Value, out var brainNodes)
            && brainNodes.TryGetValue(machineId.Trim(), out var existing))
        {
            brainNodes[existing.MachineId] = existing with { ExplicitlyOffline = true };
        }

        _relayRefreshedAt.TryRemove(brainId.Value, out _);
        return Task.CompletedTask;
    }

    public async Task<ConnectedComputeNode[]> ListNodesAsync(
        BrainId brainId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ct.ThrowIfCancellationRequested();

        await RefreshFromRelayIfLinkedAsync(brainId, ct).ConfigureAwait(false);

        if (!_nodesByBrain.TryGetValue(brainId.Value, out var brainNodes) || brainNodes.IsEmpty)
        {
            return [];
        }

        var now = _clock.GetUtcNow();
        var ttl = HeartbeatTtl;

        return [.. brainNodes.Values
            .Select(node => ToPublicNode(node, now, ttl))
            .OrderByDescending(static n => n.IsEligibleForSovereignCompute)
            .ThenByDescending(static n => n.LastHeartbeatAt)
            .ThenBy(static n => n.MachineId, StringComparer.Ordinal)];
    }

    public async Task<ComputeRouteDecision> ResolveRouteAsync(
        BrainId brainId,
        CancellationToken ct = default)
    {
        var nodes = await ListNodesAsync(brainId, ct).ConfigureAwait(false);
        var eligible = nodes.FirstOrDefault(static n => n.IsEligibleForSovereignCompute);
        if (eligible is not null)
        {
            return new ComputeRouteDecision(
                ComputeExecutionTarget.ConnectedClient,
                eligible,
                $"Routing sovereign Plane 1 compute (ICodeSandbox) to connected {eligible.ConsentMode} client '{eligible.DeviceName}' ({eligible.MachineId}).");
        }

        var reason = nodes.Length == 0
            ? "No connected compute nodes registered for brain; falling back to cloud or local sandbox."
            : "No online consented ICodeSandbox client available; falling back to cloud or local sandbox.";

        return new ComputeRouteDecision(ComputeExecutionTarget.CloudSandbox, null, reason);
    }

    public async Task<ConnectedSandboxResponse?> TryExecuteOnConnectedClientAsync(
        BrainId brainId,
        ConnectedSandboxRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId.Value);
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (transport is null)
        {
            return null;
        }

        var nodes = await ListNodesAsync(brainId, ct).ConfigureAwait(false);
        var candidates = nodes.Where(static n => n.IsEligibleForSovereignCompute).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var availabilityBudget = AvailabilityTimeout;
        foreach (var candidate in candidates)
        {
            if (!await ProbeWithinBudgetAsync(brainId, candidate, availabilityBudget, ct).ConfigureAwait(false))
            {
                await MarkOfflineAsync(brainId, candidate.MachineId, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var response = await transport.ExecuteAsync(brainId, candidate, request, ct).ConfigureAwait(false);
                if (response is null)
                {
                    await MarkOfflineAsync(brainId, candidate.MachineId, ct).ConfigureAwait(false);
                    continue;
                }

                return string.IsNullOrWhiteSpace(response.ExecutedByMachineId)
                    ? response with { ExecutedByMachineId = candidate.MachineId }
                    : response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (IsDisconnectOrUnavailable(error, ct))
            {
                (logger ?? OsConsoleLog.For<ConnectedComputeRouter>()).LogSwallowed(
                    error,
                    $"connected client '{candidate.MachineId}' failed during sandbox dispatch for brain '{brainId.Value}'; falling back");
                await MarkOfflineAsync(brainId, candidate.MachineId, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<ProofResult> RecordAndReturnValidAsync(
        BrainId brainId,
        ComputeCapabilityAdvertisement advertisement,
        ProofResult result,
        CancellationToken ct)
    {
        await RecordHeartbeatAsync(brainId, advertisement, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<bool> ProbeWithinBudgetAsync(
        BrainId brainId,
        ConnectedComputeNode candidate,
        TimeSpan availabilityBudget,
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(availabilityBudget);

        try
        {
            return await transport!.ProbeAsync(brainId, candidate, probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsDisconnectOrUnavailable(error, ct))
        {
            (logger ?? OsConsoleLog.For<ConnectedComputeRouter>()).LogSwallowed(
                error,
                $"connected client '{candidate.MachineId}' unavailable or timed out ({availabilityBudget.TotalMilliseconds:F0} ms budget) for brain '{brainId.Value}'");
            return false;
        }
    }

    private async Task RefreshFromRelayIfLinkedAsync(BrainId brainId, CancellationToken ct)
    {
        if (cloud is null || credentials is null)
        {
            return;
        }

        if (await credentials.Read(ct).ConfigureAwait(false) is null)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        if (_relayRefreshedAt.TryGetValue(brainId.Value, out var lastRefresh) && now - lastRefresh < RelayCacheFor)
        {
            return;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(AvailabilityTimeout);

        try
        {
            var answer = await cloud.BrainComputeNodesAsync(brainId.Value, bounded.Token).ConfigureAwait(false);
            _relayRefreshedAt[brainId.Value] = now;
            if (answer is null || !string.Equals(answer.Brain, brainId.Value, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var advertised in answer.Nodes)
            {
                if (string.IsNullOrWhiteSpace(advertised.MachineId))
                {
                    continue;
                }

                if (thisMachine is not null && string.Equals(advertised.MachineId, thisMachine.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                await RecordHeartbeatAsync(brainId, advertised, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (IsDisconnectOrUnavailable(error, ct) || error is InvalidDataException or JsonException)
        {
            _relayRefreshedAt[brainId.Value] = now;
            (logger ?? OsConsoleLog.For<ConnectedComputeRouter>()).LogSwallowed(
                error,
                $"refreshing connected compute nodes from relay for brain '{brainId.Value}'");
        }
    }

    private static bool IsDisconnectOrUnavailable(Exception error, CancellationToken callerCt)
        => error is HttpRequestException
            or IOException
            or TimeoutException
            or DeviceUnavailableException
            or DeviceLinkRevokedException
            || (error is OperationCanceledException && !callerCt.IsCancellationRequested);

    private static ConnectedComputeNode ToPublicNode(TrackedComputeNode tracked, DateTimeOffset now, TimeSpan ttl)
    {
        var isOnline = !tracked.ExplicitlyOffline && (now - tracked.LastHeartbeatAt) <= ttl;
        return new ConnectedComputeNode(
            BrainId: tracked.BrainId,
            MachineId: tracked.MachineId,
            DeviceName: tracked.DeviceName,
            ConsentMode: tracked.ConsentMode,
            SandboxReady: tracked.SandboxReady,
            SandboxRuntime: tracked.SandboxRuntime,
            Status: isOnline ? ComputeNodeStatus.Online : ComputeNodeStatus.Offline,
            LastHeartbeatAt: tracked.LastHeartbeatAt,
            KeyThumbprint: tracked.KeyThumbprint,
            AllowClientCodeSandbox: tracked.AllowClientCodeSandbox,
            AdvertisedCapabilities: tracked.AdvertisedCapabilities);
    }

    private sealed record TrackedComputeNode(
        string BrainId,
        string MachineId,
        string DeviceName,
        ComputeConsentMode ConsentMode,
        bool SandboxReady,
        string SandboxRuntime,
        DateTimeOffset LastHeartbeatAt,
        string? KeyThumbprint,
        bool AllowClientCodeSandbox,
        string[]? AdvertisedCapabilities,
        bool ExplicitlyOffline);
}
