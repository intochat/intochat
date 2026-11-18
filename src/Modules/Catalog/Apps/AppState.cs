using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using DigitalBrain.State;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Apps;

[GenerateSerializer, Alias("apps.state2")]
[ExportableState]
public sealed class AppState
{
    [Id(0)] public AppManifest? Manifest { get; set; }
    [Id(1)] public string Spec { get; set; } = "";
    [Id(5)] public string Version { get; set; } = "0.0.0";
    [Id(6)] public AppStatus Status { get; set; }
    [Id(7)] public string[] Activity { get; set; } = [];
    [Id(8)] public AppVersionRecord[] History { get; set; } = [];
    [Id(9)] public Guid BuildAttempt { get; set; }
    [Id(10)] public DateTimeOffset BuildStartedAt { get; set; }
    [Id(11)] public string? BrainScopeId { get; set; }
    // Read-only v1 shape of the old stored caller (JSON "principal"); the next activation turns it into PublisherSource and clears it.
    [Id(12), JsonInclude, JsonPropertyName("principal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal StoredPrincipalV1? LegacyPrincipal { get; set; }
    // The account and brain the app was created in: source-brain data for its releases, never an identity anything acts as.
    [Id(13), JsonInclude] internal AppPublisher? PublisherSource { get; set; }
    [Id(17)] public OperationReceipt[] Receipts { get; set; } = [];
    [Id(18)] public string? PublishedVersion { get; set; }
    [Id(19)] public AppOrigin? Origin { get; set; }
    [Id(20)] public bool Edited { get; set; }
    [Id(21)] public AppVersionRecord? Published { get; set; }
    [Id(22)] public string? Publisher { get; set; }
    // 06-08: requests kept for synapses that start later, as plain AppRequested records (was RetainedSignal).
    [Id(23)] public RetainedRequest[] RequestRetention { get; set; } = [];
    [Id(24)] public long RequestSequence { get; set; }
    // 06-08: the last activity, held so an interrupted turn can be reconciled on the next activation (was a RetainedSignal array).
    [Id(25)] public Signals.AppActivity? LastActivity { get; set; }
    [Id(26)] public long ActivitySequence { get; set; }
    [Id(28)] public AppReleaseRecord[] Releases { get; set; } = [];
    [Id(29)] public AppInstallReceipt? Installation { get; set; }

    [Id(30)] public long ProgramRevision { get; set; }
    [Id(31)] public AppPresentation? Presentation { get; set; }
    [Id(38)] public StringEntry[] FileEntries { get; set; } = [];
    [Id(39)] public StringEntry[] SettingEntries { get; set; } = [];
    [Id(40)] public StringEntry[] ConnectionEntries { get; set; } = [];
    [Id(43)] public long SignalCursor { get; set; }
    [Id(44)] public DeadLetterEntry[] DeadLetters { get; set; } = [];
    // The IFlows id of the latest build; the build status is the flow's.
    [Id(45)] public string? BuildFlowId { get; set; }
    // Why a record stored by an older OS was refused on activation; null for a usable app. A Failed status alone stays retryable.
    [Id(46)] public string? Refused { get; set; }

    // The maps as callers read and replace them. Each read is a copy: change a map by assigning a new one.
    [IgnoreDataMember, System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> Files
    {
        get => StringEntry.ToMap(FileEntries);
        set => FileEntries = StringEntry.From(value);
    }

    [IgnoreDataMember, System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> Settings
    {
        get => StringEntry.ToMap(SettingEntries);
        set => SettingEntries = StringEntry.From(value);
    }

    [IgnoreDataMember, System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> Connections
    {
        get => StringEntry.ToMap(ConnectionEntries);
        set => ConnectionEntries = StringEntry.From(value);
    }

    public void PutFile(string path, string source) => Files = With(Files, path, source);
    public void PutSetting(string name, string value) => Settings = With(Settings, name, value);
    public void PutConnection(string slot, string id) => Connections = With(Connections, slot, id);

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> map, string key, string value)
    {
        var copy = new Dictionary<string, string>(map, StringComparer.Ordinal) { [key] = value };
        return copy;
    }

    // The one-time v1 migration: the old stored caller keeps only its account and brain as the publisher source.
    // True when the state changed and must be stored.
    public bool MigrateLegacyPrincipal()
    {
        if (LegacyPrincipal is not { } legacy) { return false; }
        PublisherSource ??= legacy.AccountId is { Length: > 0 } account && legacy.BrainId is { Length: > 0 } brain
            ? new AppPublisher(account, brain)
            : null;
        LegacyPrincipal = null;
        return true;
    }

    public bool HasReceipt(Guid operationId)
    {
        for (var i = 0; i < Receipts.Length; i++)
        {
            if (Receipts[i].OperationId == operationId)
            {
                return true;
            }
        }
        return false;
    }

    public AppState Clone() => new()
    {
        Manifest = Manifest,
        Spec = Spec,
        FileEntries = [.. FileEntries],
        SettingEntries = [.. SettingEntries],
        ConnectionEntries = [.. ConnectionEntries],
        Version = Version,
        Status = Status,
        Activity = [.. Activity],
        History = [.. History],
        BuildAttempt = BuildAttempt,
        BuildStartedAt = BuildStartedAt,
        BrainScopeId = BrainScopeId,
        LegacyPrincipal = LegacyPrincipal,
        PublisherSource = PublisherSource,
        Receipts = [.. Receipts],
        PublishedVersion = PublishedVersion,
        Origin = Origin,
        Edited = Edited,
        Published = Published,
        Publisher = Publisher,
        RequestRetention = [.. RequestRetention],
        RequestSequence = RequestSequence,
        LastActivity = LastActivity,
        ActivitySequence = ActivitySequence,
        Releases = [.. Releases],
        Installation = Installation,
        ProgramRevision = ProgramRevision,
        Presentation = Presentation,
        SignalCursor = SignalCursor,
        DeadLetters = [.. DeadLetters],
        BuildFlowId = BuildFlowId,
        Refused = Refused,
    };
}

[GenerateSerializer, Alias("apps.version-record")]
public sealed record AppVersionRecord(
    [property: Id(0)] string Version,
    [property: Id(1)] DateTimeOffset SavedAt,
    [property: Id(2)] AppManifest Manifest,
    [property: Id(3)] string Spec,
    [property: Id(5)] StringEntry[] FileEntries)
{
    // Internal, so the record's one public constructor is the one JSON reads it with (06-08: the plain app's state).
    internal AppVersionRecord(string version, DateTimeOffset savedAt, AppManifest manifest, string spec, IEnumerable<KeyValuePair<string, string>> files)
        : this(version, savedAt, manifest, spec, StringEntry.From(files)) { }

    [IgnoreDataMember, System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, string> Files => StringEntry.ToMap(FileEntries);
}

[GenerateSerializer, Alias("apps.release-record")]
public sealed record AppReleaseRecord(
    [property: Id(0)] AppPublication Publication,
    [property: Id(1)] AppBundle Bundle);

[GenerateSerializer, Alias("into.apps.dead-letter")]
public sealed record DeadLetterEntry(
    [property: Id(0)] long Position,
    [property: Id(1)] string Publisher,
    [property: Id(2)] string SignalType,
    [property: Id(3)] string ErrorMessage,
    [property: Id(4)] DateTimeOffset At);

[GenerateSerializer, Alias("apps.retained-request")]
public sealed record RetainedRequest([property: Id(0)] long Sequence, [property: Id(1)] Signals.AppRequested Request);

// The account and brain an app was created in. Data only: nothing acts as it and no caller is rebuilt from it.
[GenerateSerializer, Alias("apps.publisher")]
internal sealed record AppPublisher([property: Id(0)] string Account, [property: Id(1)] string Brain);

// The stored shape of the caller AppState kept before the one-caller model (camelCase JSON in the journal). Read once, never written.
[GenerateSerializer, Alias("apps.stored-principal-v1")]
internal sealed record StoredPrincipalV1(
    [property: Id(0)] string? PrincipalId,
    [property: Id(1)] string? AccountId,
    [property: Id(2)] string? BrainId,
    [property: Id(3)] string? AppId,
    [property: Id(4)] string? ConversationId);
