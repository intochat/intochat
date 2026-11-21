using System.Text.Json;
using System.Text.Json.Serialization;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Devices.Link;

// Every route under /circle/v1/ shared between a device and its owner's cloud server.
// Every request after enrollment carries a bearer access token and an X-Machine-Proof header (MachineProof).
public static class CircleRoutes
{
    public const string Prefix = "/circle/v1";
    public const string Enroll = Prefix + "/enroll";
    public const string Refresh = Prefix + "/refresh";
    public const string Leave = Prefix + "/leave";
    public const string Status = Prefix + "/status";
    public const string Journal = Prefix + "/journal/{brainId}";
    public const string JournalSnapshot = Prefix + "/journal/{brainId}/snapshot";
    public const string Store = Prefix + "/store";
    public const string StorePackage = Prefix + "/store/{publisher}/{name}/{version}";
    // The files route takes the rest of the path after the brain id.
    public const string Files = Prefix + "/files/{brainId}/{**path}";
    public const string MachineProofHeader = "X-Machine-Proof";

    public static string JournalFor(string brainId) => Prefix + "/journal/" + Uri.EscapeDataString(brainId);

    public static string JournalSnapshotFor(string brainId) => JournalFor(brainId) + "/snapshot";

    public static string StorePackageFor(string publisher, string name, string version)
        => Prefix + "/store/" + Uri.EscapeDataString(publisher) + "/" + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(version);

    public static string FilesFor(string brainId, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return Prefix + "/files/" + Uri.EscapeDataString(brainId) + "/" + string.Join('/', segments);
    }
}

public static class DeviceRoutes
{
    public const string Models = CircleRoutes.Prefix + "/models";
    public const string Next = CircleRoutes.Prefix + "/compute/next";
    public const string Stream = CircleRoutes.Prefix + "/compute/{jobId}/stream";
    public const int WaitSeconds = 25;

    public static string StreamFor(string jobId)
        => CircleRoutes.Prefix + "/compute/" + Uri.EscapeDataString(jobId) + "/stream";
}

// MachinePublicKeySpki is the base64url SubjectPublicKeyInfo of the machine P-256 key.
public sealed record EnrollRequest(string Code, string MachinePublicKeySpki, string MachineId, string MachineName, string OsDescription);

public sealed record CircleInfo(string Id, string Name, string Role);

public sealed record CircleEndpoints(string Journal, string Store, string Files);

public sealed record EnrollResponse(
    string MachineCredentialId,
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt,
    CircleInfo Circle,
    bool PendingAccount,
    CircleEndpoints Endpoints);

[JsonConverter(typeof(JsonStringEnumConverter<EnrollErrorKind>))]
public enum EnrollErrorKind
{
    [JsonStringEnumMemberName("rejected")] Rejected,
    [JsonStringEnumMemberName("expired")] Expired,
    [JsonStringEnumMemberName("rate_limited")] RateLimited,
}

public sealed record EnrollError(EnrollErrorKind Kind);

// The refresh answer is an EnrollResponse with rotated tokens.
public sealed record RefreshRequest(string RefreshToken);

public sealed record CircleStatusResponse(string CircleId, string CircleName, string Role, bool PendingAccount, bool Revoked, string MachineId);

[GenerateSerializer, Alias("devices.link.advertised-model")]
public sealed record AdvertisedModel(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Family,
    [property: Id(3)] Abilities Can);

[GenerateSerializer, Alias("devices.link.models-report")]
public sealed record ModelsReport(
    [property: Id(0)] string DeviceName,
    [property: Id(1)] AdvertisedModel[] Models);

[GenerateSerializer, Alias("devices.link.device-message")]
public sealed record DeviceMessage(
    [property: Id(0)] string Role,
    [property: Id(1)] string Text);

[GenerateSerializer, Alias("devices.link.device-chat-job")]
public sealed record DeviceChatJob(
    [property: Id(0)] string JobId,
    [property: Id(1)] string Model,
    [property: Id(2)] DeviceMessage[] Messages,
    [property: Id(3)] float? Temperature,
    [property: Id(4)] int? MaxOutputTokens,
    [property: Id(5)] string? TraceParent);

[GenerateSerializer, Alias("devices.link.device-chunk")]
public sealed record DeviceChunk(
    [property: Id(0)] string? Delta,
    [property: Id(1)] bool Done = false,
    [property: Id(2)] string? Error = null,
    [property: Id(3)] int? InputTokens = null,
    [property: Id(4)] int? OutputTokens = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(EnrollError))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(CircleStatusResponse))]
[JsonSerializable(typeof(MachineProofHeader))]
[JsonSerializable(typeof(MachineProofPayload))]
[JsonSerializable(typeof(AdvertisedModel))]
[JsonSerializable(typeof(ModelsReport))]
[JsonSerializable(typeof(DeviceMessage))]
[JsonSerializable(typeof(DeviceChatJob))]
[JsonSerializable(typeof(DeviceChunk))]
[JsonSerializable(typeof(LinkState))]
[JsonSerializable(typeof(LinkStatus))]
public sealed partial class DeviceWireJson : JsonSerializerContext;
