using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Sandbox;

/// <summary>Wire frame sent between the host and the out-of-process <c>--unit-host</c> worker over stdin.</summary>
public sealed record UnitHostFrameRequest(
    string Id,
    string Op,
    DeployAppRequest? Deploy = null,
    DispatchAppRequest? Dispatch = null,
    UnloadAppRequest? Unload = null,
    bool? Ok = null,
    int? StatusCode = null,
    SandboxInvokeResponse? InvokeResult = null,
    string? Error = null);

/// <summary>Wire frame returned from the <c>--unit-host</c> worker over stdout.</summary>
public sealed record UnitHostFrameResponse(
    string Id,
    bool Ok,
    string? Error = null,
    DeployAppResponse? Deploy = null,
    DispatchAppResponse? Dispatch = null,
    UnloadAppResponse? Unload = null,
    IReadOnlyDictionary<string, string>? EnvironmentSnapshot = null,
    int? ProcessId = null,
    string? CallbackKind = null,
    string? BrainId = null,
    string? AppKey = null,
    string? AppToken = null,
    SandboxInvokeRequest? Invoke = null,
    SandboxSignalRequest? Signal = null,
    SandboxLogRequest? Log = null);

internal static class UnitHostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
