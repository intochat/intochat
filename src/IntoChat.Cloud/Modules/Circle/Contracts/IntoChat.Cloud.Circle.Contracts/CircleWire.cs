using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntoChat.Cloud.Circle;

// The circle wire (D-06, D-09): every route the os circle module calls on a circle server, under /circle/v1/.
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

// One encrypted batch of journal signals (FromExclusive, LastSignal]; the server stores ciphertext only.
public sealed record JournalBatch(long FromExclusive, long LastSignal, int Count, string Nonce, string Ciphertext, string KeyId);

public sealed record JournalAck(long HighWater);

// 409: the server holds a different high water; the client resumes from it.
public sealed record JournalConflict(long HighWater);

public sealed record SnapshotUpload(long AtSignal, string Nonce, string Ciphertext, string KeyId);

public sealed record CloudListing(
    string Publisher,
    string Name,
    string Version,
    string Digest,
    long Size,
    string Summary,
    IReadOnlyList<string> RequiresCapabilities,
    DateTimeOffset PublishedAt);

public sealed record StorePage(IReadOnlyList<CloudListing> Items, string? NextCursor);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(EnrollRequest))]
[JsonSerializable(typeof(EnrollResponse))]
[JsonSerializable(typeof(EnrollError))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(JournalBatch))]
[JsonSerializable(typeof(JournalAck))]
[JsonSerializable(typeof(JournalConflict))]
[JsonSerializable(typeof(SnapshotUpload))]
[JsonSerializable(typeof(StorePage))]
[JsonSerializable(typeof(CloudListing))]
[JsonSerializable(typeof(MachineProofHeader))]
[JsonSerializable(typeof(MachineProofPayload))]
public sealed partial class CircleJsonContext : JsonSerializerContext;
