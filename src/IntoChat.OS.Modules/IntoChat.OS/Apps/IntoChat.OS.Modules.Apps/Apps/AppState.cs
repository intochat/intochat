using IntoChat.OS.Framework;

namespace IntoChat.OS.Apps;

[GenerateSerializer, Alias("apps.state2")]
public sealed class AppState
{
    [Id(0)] public AppManifest? Manifest { get; set; }
    [Id(1)] public string Spec { get; set; } = "";
    [Id(2)] public Dictionary<string, string> Files { get; set; } = [];
    [Id(3)] public Dictionary<string, string> Settings { get; set; } = [];
    [Id(4)] public Dictionary<string, string> Connections { get; set; } = [];
    [Id(5)] public string Version { get; set; } = "0.0.0";
    [Id(6)] public AppStatus Status { get; set; }
    [Id(7)] public string[] Activity { get; set; } = [];
    [Id(8)] public AppVersionRecord[] History { get; set; } = [];
    [Id(9)] public Guid BuildAttempt { get; set; }
    [Id(10)] public DateTimeOffset BuildStartedAt { get; set; }
    [Id(11)] public string? BrainScopeId { get; set; }
    // The caller that created the app: what its running program acts as, so a restart keeps its identity.
    [Id(12)] public CallerContext? Principal { get; set; }
    [Id(17)] public List<OperationReceipt> Receipts { get; set; } = [];
    [Id(18)] public string? PublishedVersion { get; set; }
    [Id(19)] public AppOrigin? Origin { get; set; }
    [Id(20)] public bool Edited { get; set; }
    [Id(21)] public AppVersionRecord? Published { get; set; }
    [Id(22)] public string? Publisher { get; set; }
    [Id(23)] public RetainedSignal[] RequestRetention { get; set; } = [];
    [Id(24)] public long RequestSequence { get; set; }
    [Id(25)] public RetainedSignal[] ActivityRetention { get; set; } = [];
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

    public AppState Clone() => new()
    {
        Manifest = Manifest,
        Spec = Spec,
        Files = new(Files, StringComparer.Ordinal),
        Settings = new(Settings, StringComparer.Ordinal),
        Connections = new(Connections, StringComparer.Ordinal),
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
        ActivityRetention = [.. ActivityRetention],
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
    [property: Id(4)] Dictionary<string, string> Files);

[GenerateSerializer, Alias("apps.release-record")]
public sealed record AppReleaseRecord(
    [property: Id(0)] AppPublication Publication,
    [property: Id(1)] AppBundle Bundle);
