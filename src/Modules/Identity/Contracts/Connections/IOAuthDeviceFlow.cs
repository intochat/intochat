using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;

namespace IntoChat.OS.Modules.Identity.Contracts.Connections;

/// <summary>Outcome of a single RFC 8628 device authorization poll.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OAuthDevicePollStatus>))]
public enum OAuthDevicePollStatus
{
    Pending = 0,
    Connected = 1,
    Denied = 2,
    Expired = 3,
}

/// <summary>
/// Public device authorization prompt returned by <see cref="IOAuthDeviceFlow.StartAsync"/>.
/// Never carries the provider <c>device_code</c> or any token material.
/// </summary>
public sealed record OAuthDeviceStartResult(
    string FlowId,
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>
/// Result of a single poll attempt via <see cref="IOAuthDeviceFlow.PollOnceAsync"/>.
/// Never carries raw access or refresh tokens.
/// </summary>
public sealed record OAuthDevicePollResult(
    OAuthDevicePollStatus Status,
    int IntervalSeconds,
    ConnectionView? Connection,
    string? Error);

/// <summary>
/// OAuth 2.0 Device Authorization Grant (RFC 8628) flow service for zero-secret desktop/CLI login.
/// </summary>
public interface IOAuthDeviceFlow
{
    Task<OAuthDeviceStartResult> StartAsync(
        BrainId brainId,
        string connectionKey,
        string authMethodId,
        string? target = null,
        CancellationToken ct = default);

    Task<OAuthDevicePollResult> PollOnceAsync(
        BrainId brainId,
        string flowId,
        CancellationToken ct = default);

    Task<OAuthDevicePollResult> PollOnceAsync(
        BrainId brainId,
        string flowId,
        string? expectedConnectionId,
        CancellationToken ct = default)
        => PollOnceAsync(brainId, flowId, ct);
}
