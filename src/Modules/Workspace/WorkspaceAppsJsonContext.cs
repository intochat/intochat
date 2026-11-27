using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps;

internal sealed record WorkspaceErrorResponse(string Error);

internal sealed record PackageCompileResponse(
    bool Succeeded,
    IReadOnlyList<string> Errors,
    AppPackage? Package,
    SlotResolutionResult? Slots);

internal sealed record AppCatalogOriginResponse(
    string Publisher,
    string Name,
    string Version);

internal sealed record AppCatalogItemResponse(
    string Name,
    string Title,
    string Description,
    string Status,
    string Version,
    string[] Operations,
    string? Icon,
    string? PublishedVersion,
    AppCatalogOriginResponse? Origin,
    bool Edited,
    bool UpdateAvailable,
    bool ShippedUpdate);

internal sealed record AppViewResponse(
    string Name,
    AppManifest Manifest,
    string Spec,
    Dictionary<string, string> Files,
    Dictionary<string, string> Settings,
    Dictionary<string, string> Connections,
    string Version,
    string Status,
    string[] Activity,
    AppVersionSummary[] Versions,
    string? PublishedVersion,
    AppOrigin? Origin,
    bool Edited,
    DigitalBrain.UI.View? View,
    DigitalBrain.UI.View? Side,
    long ViewRevision);

internal sealed record AppDeletedResponse(string Deleted);

internal sealed record AppRequestAcceptedResponse(string RequestId);

internal sealed record AppImportConsentResponse(
    string Status,
    AppRequirements? Requires,
    string Consent,
    string? Error);

internal sealed record AppInstallOkResponse(
    string Status,
    string? InstalledName);

internal sealed record AppInstallConsentResponse(
    string Status,
    AppRequirements? Requires,
    string? Error);

internal sealed record AppUpdateConsentResponse(
    string Status,
    AppRequirements? Added,
    AppRequirements? Requires,
    string? Error);

internal sealed record AppInstallRefusedResponse(
    string Status,
    string? Error);

internal sealed record RuleViolationResponse(
    string Operation,
    string[] Rules,
    string[] Messages);

internal sealed record PolicyRetiredResponse(string Retired);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, MaxDepth = 128)]
[JsonSerializable(typeof(WorkspaceErrorResponse))]
[JsonSerializable(typeof(PackageCompileResponse))]
[JsonSerializable(typeof(AppCatalogOriginResponse))]
[JsonSerializable(typeof(AppCatalogItemResponse))]
[JsonSerializable(typeof(List<AppCatalogItemResponse>))]
[JsonSerializable(typeof(AppViewResponse))]
[JsonSerializable(typeof(AppDeletedResponse))]
[JsonSerializable(typeof(AppRequestAcceptedResponse))]
[JsonSerializable(typeof(AppImportConsentResponse))]
[JsonSerializable(typeof(AppInstallOkResponse))]
[JsonSerializable(typeof(AppInstallConsentResponse))]
[JsonSerializable(typeof(AppUpdateConsentResponse))]
[JsonSerializable(typeof(AppInstallRefusedResponse))]
[JsonSerializable(typeof(RuleViolationResponse))]
[JsonSerializable(typeof(PolicyRetiredResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorkspaceAppsJsonContext : JsonSerializerContext;
