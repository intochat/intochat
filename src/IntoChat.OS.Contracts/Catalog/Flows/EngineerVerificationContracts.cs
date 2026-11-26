using System.Globalization;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.Modules.Apps.Flows;

[GenerateSerializer, Alias("engineer.build-report")]
public sealed record EngineerBuildReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool DotnetBuildPassed,
    [property: Id(2)] bool FlutterAnalyzePassed,
    [property: Id(3)] bool DotnetTestPassed,
    [property: Id(4)] int PassedTests,
    [property: Id(5)] int FailedTests,
    [property: Id(6)] int WarningCount,
    [property: Id(7)] int ErrorCount,
    [property: Id(8)] string[] Diagnostics,
    [property: Id(9)] long DurationMs,
    [property: Id(10)] string Summary)
{
    public EngineerBuildReport() : this(false, false, false, false, 0, 0, 0, 0, [], 0, "") { }
}

[GenerateSerializer, Alias("engineer.live-probe")]
public sealed record EngineerLiveProbe(
    [property: Id(0)] string Name,
    [property: Id(1)] string Kind,
    [property: Id(2)] bool Passed,
    [property: Id(3)] int? HttpStatus,
    [property: Id(4)] string Detail)
{
    public EngineerLiveProbe() : this("", "mcp", false, null, "") { }
}

[GenerateSerializer, Alias("engineer.live-report")]
public sealed record EngineerLiveReport(
    [property: Id(0)] bool Succeeded,
    [property: Id(1)] bool HostHealthy,
    [property: Id(2)] bool McpProbesPassed,
    [property: Id(3)] bool PlaywrightPassed,
    [property: Id(4)] int Http4xxCount,
    [property: Id(5)] int Http5xxCount,
    [property: Id(6)] int ErrorSpanCount,
    [property: Id(7)] int ErrorLogCount,
    [property: Id(8)] EngineerLiveProbe[] Probes,
    [property: Id(9)] string[] TraceSummaries,
    [property: Id(10)] string Summary)
{
    public EngineerLiveReport() : this(false, false, false, false, 0, 0, 0, 0, [], [], "") { }
}

// Which verification gates an engineering run passes before it may ask a person to approve its commit. Every gate is on unless
// the run was started with one switched off; the run keeps the policy and who switched a gate off (SkippedBy, stamped by the
// flow from its caller, never read from the start payload). A skipped gate is journaled as a skipped step and named on the
// run's summary, its approval card and its pull request.
//
// LiveVerifyNotAvailable is an outcome, not a choice: the flow records it when live-verify was on and the installation has no
// live environment configured to check against (ILiveEnvironment), so the gate could not run. It is neither passed nor skipped
// by a person. It is never read from a start payload; a run stored before it existed reads as false, and it is written only
// when true, so every other run is stored as before.
[GenerateSerializer, Alias("engineer.verification-policy")]
public sealed record EngineerVerificationPolicy(
    [property: Id(0)] bool BuildAndTest = true,
    [property: Id(1)] bool LiveVerify = true,
    [property: Id(2)] string? SkippedBy = null,
    [property: Id(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LiveVerifyNotAvailable = false)
{
    public const string BuildAndTestGate = "build-and-test";
    public const string LiveVerifyGate = "live-verify";

    public const string NotLiveVerified =
        "NOT LIVE-VERIFIED: no live environment is available on this installation, so live-verify did not run and this commit was not checked against a running system.";

    public EngineerVerificationPolicy() : this(true, true, null, false) { }

    public static EngineerVerificationPolicy Full { get; } = new();

    [JsonIgnore]
    public bool SkipsAny => !BuildAndTest || !LiveVerify;

    // Whether a gate did not check this run's commit, for either reason: switched off by a person, or not available.
    [JsonIgnore]
    public bool LeavesUnchecked => SkipsAny || LiveVerifyNotAvailable;

    [JsonIgnore]
    public string[] SkippedGates =>
    [
        .. BuildAndTest ? Array.Empty<string>() : [BuildAndTestGate],
        .. LiveVerify ? Array.Empty<string>() : [LiveVerifyGate],
    ];

    public bool Runs(string gate) => gate switch
    {
        BuildAndTestGate => BuildAndTest,
        LiveVerifyGate => LiveVerify,
        _ => true,
    };

    // One line for a person: which gates ran, which were switched off and by whom, and whether live-verify was not available.
    public string Describe()
    {
        var skipped = SkipsAny
            ? $"Verification SKIPPED for {string.Join(" and ", SkippedGates)} by {(string.IsNullOrWhiteSpace(SkippedBy) ? "the starter" : SkippedBy)}: this commit was not checked by {(SkippedGates.Length == 1 ? "that gate" : "those gates")}."
            : null;
        return (skipped, LiveVerifyNotAvailable) switch
        {
            (null, false) => "Verification: build-and-test and live-verify are on.",
            (null, true) => NotLiveVerified,
            (not null, false) => skipped,
            (not null, true) => skipped + " " + NotLiveVerified,
        };
    }
}
