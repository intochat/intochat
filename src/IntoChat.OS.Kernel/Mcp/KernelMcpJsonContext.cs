using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Kernel.Auth;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Screens;
using IntoChat.Sdk;

namespace IntoChat.OS.Kernel.Mcp;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(IReadOnlyList<NeuronInstance>))]
[JsonSerializable(typeof(NeuronInstance[]))]
[JsonSerializable(typeof(NeuronInstance))]
[JsonSerializable(typeof(IReadOnlyList<NeuronTypeHit>))]
[JsonSerializable(typeof(NeuronTypeHit[]))]
[JsonSerializable(typeof(NeuronTypeHit))]
[JsonSerializable(typeof(IReadOnlyList<ScreenSummary>))]
[JsonSerializable(typeof(ScreenSummary[]))]
[JsonSerializable(typeof(ScreenSummary))]
[JsonSerializable(typeof(ScreenSnapshot))]
[JsonSerializable(typeof(ScreenActionResult))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(View))]
[JsonSerializable(typeof(PublishedView))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Member))]
[JsonSerializable(typeof(Grant))]
[JsonSerializable(typeof(Grant[]))]
[JsonSerializable(typeof(IReadOnlyList<Grant>))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeInvokeResponse))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeSignalRequest))]
[JsonSerializable(typeof(SandboxEdgeEndpoints.EdgeLogRequest))]
[JsonSerializable(typeof(AppPackage))]
[JsonSerializable(typeof(AppInstallRequest))]
[JsonSerializable(typeof(AppBundle))]
[JsonSerializable(typeof(NotificationRequest))]
[JsonSerializable(typeof(InternalPageReport))]
internal sealed partial class KernelMcpJsonContext : JsonSerializerContext;
