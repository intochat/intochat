using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    IReadOnlyList<string>? ChangedFiles = null);

internal sealed record EngineerPlannerMemorySnapshot(
    string? BriefKey,
    string? BriefContent,
    string? ProjectNote);

