using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Kernel.Sandbox;

// Web defaults (camelCase, case-insensitive) match the JsonSerializerOptions.Web shape the sandbox worker sends and reads.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeResponse))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeSignalRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeLogRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeSignalAck))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeLogAck))]
internal sealed partial class SandboxEdgeJsonContext : JsonSerializerContext;
