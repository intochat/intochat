using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.UI;

namespace IntoChat.OS.Kernel.Sandbox;

// Web defaults (camelCase, case-insensitive) match the JsonSerializerOptions.Web shape the sandbox worker sends and reads.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeResponse))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeSignalRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeLogRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeSignalAck))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeLogAck))]
[JsonSerializable(typeof(DeployWorkerRequest))]
[JsonSerializable(typeof(DeployWorkerResponse))]
[JsonSerializable(typeof(DispatchWorkerRequest))]
[JsonSerializable(typeof(DispatchWorkerResponse))]
[JsonSerializable(typeof(UnloadWorkerRequest))]
[JsonSerializable(typeof(UnloadWorkerResponse))]
[JsonSerializable(typeof(WorkerCommandDescriptor))]
[JsonSerializable(typeof(View))]
internal sealed partial class SandboxEdgeJsonContext : JsonSerializerContext;

// NDJSON stdio frame context for confined --unit-host child processes (omits null properties on the wire).
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UnitHostFrameRequest))]
[JsonSerializable(typeof(UnitHostFrameResponse))]
internal sealed partial class UnitHostFrameJsonContext : JsonSerializerContext;
