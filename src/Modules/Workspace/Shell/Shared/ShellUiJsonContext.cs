using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Modules.Flutter.Workspace;

namespace IntoChat.OS.Modules.Flutter;

internal sealed record ConnectResponse(ViewRef? Root, string? ActivationApp);

internal sealed record ActDeliveredResponse(bool Delivered);

internal sealed record UiErrorResponse(
    string Error,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

internal sealed record UnitViewportResponse(
    long Generation,
    WorkspaceViewRef? Target,
    ViewRef? Root,
    string? Error);

internal sealed record EmptyViewportResponse(
    long Revision,
    ViewRef? Root,
    string? Error);

internal sealed record TargetedViewportResponse(
    long Revision,
    WorkspaceViewRef? Target,
    ViewRef? Root,
    string? Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = 128)]
[JsonSerializable(typeof(ConnectResponse))]
[JsonSerializable(typeof(ActDeliveredResponse))]
[JsonSerializable(typeof(UiErrorResponse))]
[JsonSerializable(typeof(UnitViewportResponse))]
[JsonSerializable(typeof(EmptyViewportResponse))]
[JsonSerializable(typeof(TargetedViewportResponse))]
[JsonSerializable(typeof(AppUiEndpoints.ActRequest))]
[JsonSerializable(typeof(DesktopShellProjection))]
[JsonSerializable(typeof(PublishedView))]
[JsonSerializable(typeof(DigitalBrain.UI.View))]
[JsonSerializable(typeof(WebBrowserView))]
internal sealed partial class ShellUiJsonContext : JsonSerializerContext;
