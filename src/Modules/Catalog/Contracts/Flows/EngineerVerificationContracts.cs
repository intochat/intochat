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
