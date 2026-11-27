using System.Diagnostics;
using DigitalBrain;
using IntoChat.OS.Modules.Apps.Trace;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class EngineerTelemetry
{
    public const string SourceName = "IntoChat.OS.Engineer";

    public static readonly ActivitySource Source = new(SourceName);
}

internal sealed record EngineerVerificationCall(
    string Target,
    string Method,
    string Expected);

internal sealed record EngineerVerificationPlan(
    string HealthPath,
    IReadOnlyList<EngineerVerificationCall> McpCalls,
    bool RequireZeroErrorSpans,
    bool TouchesUi);

internal sealed record EngineerLiveContext(
    string? TraceId = null,
    ITraceReader? TraceReader = null,
    IReadOnlyList<(string ScreenName, IScreen Screen)>? Screens = null,
    EngineerVerificationPlan? VerificationPlan = null,
    string? WorktreePath = null,
    string? Goal = null,
    IReadOnlyList<string>? ChangedFiles = null,
    string? CommitSha = null,
    string? BrainId = null);

internal sealed record EngineerPlannerMemorySnapshot(
    string? BriefKey,
    string? BriefContent,
    string? ProjectNote);
