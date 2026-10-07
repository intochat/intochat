using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.CSharp.Files;

// Run is null until the file is first started. Active says the file is wanted: a stopped, finished or
// disarmed file keeps its record so Inspect and ReadLogs still address its last run.
[GenerateSerializer, Alias("microsoft.csharp.run-record")]
internal sealed record RunRecord(
    // A new run per launch, so a stale run can never be addressed again.
    [property: Id(0)] string Id,
    // The principal who started it; production keys its sandbox session (one container per user) by it.
    [property: Id(1)] string Owner,
    // The caller who started it; the script edge speaks for this caller as an app.
    [property: Id(2)] CallerContext? Caller,
    // Consecutive non-zero exits since the last Start.
    [property: Id(3)] int Failures,
    [property: Id(4)] bool Active);

[GenerateSerializer, Alias("microsoft.csharp.file-state")]
internal sealed record CSharpFileState
{
    [Id(0)] public string Program { get; init; } = "";
    [Id(1)] public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
    // Durable subscriptions the current program registered by streaming; they outlive its runs.
    [Id(2)] public Subscription[] Subscriptions { get; init; } = [];
    [Id(3)] public RunRecord? Run { get; init; }
    [Id(4)] public string? AppId { get; init; }
    [Id(5)] public string? AppBrain { get; init; }
}
