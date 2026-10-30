using System.Runtime.Serialization;
using DigitalBrain.Capabilities;
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
    // Files, settings and connections are entry arrays; the dictionaries are the older stored form, read once and
    // then superseded by the next write (the Files, Settings and Connections views below prefer the arrays).
    [Id(2), LegacyShape] public Dictionary<string, string>? LegacyFiles { get; set; }
    [Id(3), LegacyShape] public Dictionary<string, string>? LegacySettings { get; set; }
    [Id(4), LegacyShape] public Dictionary<string, string>? LegacyConnections { get; set; }
    [Id(5)] public string Version { get; set; } = "0.0.0";
    [Id(6)] public AppStatus Status { get; set; }
    [Id(7)] public string[] Activity { get; set; } = [];
    [Id(8)] public AppVersionRecord[] History { get; set; } = [];
    [Id(9)] public Guid BuildAttempt { get; set; }
    [Id(10)] public DateTimeOffset BuildStartedAt { get; set; }
    [Id(11)] public string? BrainScopeId { get; set; }
    // The caller that created the app: what its running program acts as, so a restart keeps its identity.
    [Id(12)] public CapabilityCaller? Principal { get; set; }
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
    // The fast path's requests and their outcomes, newest last.
    [Id(27)] public AppSynapse[] Synapses { get; set; } = [];
    [Id(28)] public AppReleaseRecord[] Releases { get; set; } = [];
    [Id(29)] public AppInstallReceipt? Installation { get; set; }

    [Id(30)] public long ProgramRevision { get; set; }
    [Id(31)] public AppPresentation? Presentation { get; set; }
    [Id(32)] public string? Migration { get; set; }
    [Id(33)] public bool MigrationPaused { get; set; }
    [Id(34)] public AppState? MigrationArchive { get; set; }
    [Id(35)] public string? MigrationSource { get; set; }
    [Id(36)] public string? MigrationTarget { get; set; }
    [Id(37)] public Signals.AppRequested[] MigrationRequests { get; set; } = [];
    [Id(38)] public StringEntry[] FileEntries { get; set; } = [];
    [Id(39)] public StringEntry[] SettingEntries { get; set; } = [];
    [Id(40)] public StringEntry[] ConnectionEntries { get; set; } = [];

    // The maps as callers read and replace them. Each read is a copy: change a map by assigning a new one.
    [IgnoreDataMember]
    public IReadOnlyDictionary<string, string> Files
    {
        get => StringEntry.ToMap(FileEntries, LegacyFiles);
        set { FileEntries = StringEntry.From(value); LegacyFiles = null; }
    }

    [IgnoreDataMember]
    public IReadOnlyDictionary<string, string> Settings
    {
        get => StringEntry.ToMap(SettingEntries, LegacySettings);
        set { SettingEntries = StringEntry.From(value); LegacySettings = null; }
    }

    [IgnoreDataMember]
    public IReadOnlyDictionary<string, string> Connections
    {
        get => StringEntry.ToMap(ConnectionEntries, LegacyConnections);
        set { ConnectionEntries = StringEntry.From(value); LegacyConnections = null; }
    }

    public void PutFile(string path, string source) => Files = With(Files, path, source);
    public void PutSetting(string name, string value) => Settings = With(Settings, name, value);
    public void PutConnection(string slot, string id) => Connections = With(Connections, slot, id);

    private static Dictionary<string, string> With(IReadOnlyDictionary<string, string> map, string key, string value)
    {
        var copy = new Dictionary<string, string>(map, StringComparer.Ordinal) { [key] = value };
        return copy;
    }

    public AppState Clone() => new()
    {
        Manifest = Manifest,
        Spec = Spec,
        LegacyFiles = LegacyFiles is null ? null : new(LegacyFiles, StringComparer.Ordinal),
        LegacySettings = LegacySettings is null ? null : new(LegacySettings, StringComparer.Ordinal),
        LegacyConnections = LegacyConnections is null ? null : new(LegacyConnections, StringComparer.Ordinal),
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
        Principal = Principal,
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
        Synapses = [.. Synapses],
        Releases = [.. Releases],
        Installation = Installation,
        ProgramRevision = ProgramRevision,
        Presentation = Presentation,
        Migration = Migration,
        MigrationPaused = MigrationPaused,
        MigrationArchive = MigrationArchive,
        MigrationSource = MigrationSource,
        MigrationTarget = MigrationTarget,
        MigrationRequests = [.. MigrationRequests],
    };
}

[GenerateSerializer, Alias("apps.version-record")]
public sealed record AppVersionRecord(
    [property: Id(0)] string Version,
    [property: Id(1)] DateTimeOffset SavedAt,
    [property: Id(2)] AppManifest Manifest,
    [property: Id(3)] string Spec,
    [property: Id(4), LegacyShape] Dictionary<string, string>? LegacyFiles,
    [property: Id(5)] StringEntry[] FileEntries)
{
    // Internal, so the record's one public constructor is the one JSON reads it with (06-08: the plain app's state).
    internal AppVersionRecord(string version, DateTimeOffset savedAt, AppManifest manifest, string spec, IEnumerable<KeyValuePair<string, string>> files)
        : this(version, savedAt, manifest, spec, null, StringEntry.From(files)) { }

    [IgnoreDataMember]
    public IReadOnlyDictionary<string, string> Files => StringEntry.ToMap(FileEntries, LegacyFiles);
}

[GenerateSerializer, Alias("apps.release-record")]
public sealed record AppReleaseRecord(
    [property: Id(0)] AppPublication Publication,
    [property: Id(1)] AppBundle Bundle);
