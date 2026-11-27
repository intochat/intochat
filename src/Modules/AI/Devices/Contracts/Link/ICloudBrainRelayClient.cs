namespace IntoChat.OS.Modules.Devices.Link;

/// <summary>
/// Unified outbound HTTP client contract for OS -&gt; Cloud <c>/circle/v1/compute/*</c> brain-relay calls.
/// Implementations authenticate transparently via single-machine <see cref="DeviceCredentials"/>
/// (<c>Bearer</c> + <c>X-Machine-Proof</c>) or multi-tenant <c>ServiceAssertion</c> JWTs.
/// </summary>
public interface ICloudBrainRelayClient
{
    ValueTask<bool> IsConfiguredAsync(CancellationToken ct = default);

    Task<BrainDevicesResponse> BrainModelsAsync(string brain, CancellationToken ct = default);

    // The relay's NDJSON stream of DeviceChunk lines for one chat; the caller disposes the response.
    Task<HttpResponseMessage> BrainChatAsync(BrainChatRequest chat, CancellationToken ct = default);

    Task<BrainComputeNodesResponse?> BrainComputeNodesAsync(string brain, CancellationToken ct = default);

    Task<ConnectedSandboxResponse?> DispatchBrainSandboxAsync(
        BrainSandboxDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        CancellationToken ct = default);

    Task<PcResourceResponse?> DispatchBrainResourceAsync(
        BrainResourceDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        CancellationToken ct = default);

    Task<ConnectedEngineeringResponse?> DispatchBrainEngineeringAsync(
        BrainEngineeringDispatchRequest dispatch,
        TimeSpan availabilityTimeout,
        Func<EngineeringStreamChunk, Task>? onProgress = null,
        CancellationToken ct = default)
        => Task.FromResult<ConnectedEngineeringResponse?>(null);
}
