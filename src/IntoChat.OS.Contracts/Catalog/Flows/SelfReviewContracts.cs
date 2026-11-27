using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace IntoChat.OS.Modules.Apps.Flows;

public enum ReviewFindingSource
{
    Analyzer,
    ArchitectureFact,
    Build,
    Test,
    Model,
}

public enum ReviewSeverity
{
    Info,
    Warning,
    Blocking,
}

[GenerateSerializer, Alias("engineer.review-finding")]
public sealed record ReviewFinding(
    [property: Id(0)] string RuleId,
    [property: Id(1)] ReviewFindingSource Source,
    [property: Id(2)] string Path,
    [property: Id(3)] int Line,
    [property: Id(4)] ReviewSeverity Severity,
    [property: Id(5)] string Message)
{
    public ReviewFinding() : this("", ReviewFindingSource.Analyzer, "", 0, ReviewSeverity.Info, "") { }
}

[GenerateSerializer, Alias("engineer.self-review-report")]
public sealed record SelfReviewReport([property: Id(0)] ReviewFinding[] Findings)
{
    public SelfReviewReport() : this([]) { }

    public static SelfReviewReport Empty { get; } = new([]);

    [JsonIgnore]
    public bool HasBlocking => Findings is { Length: > 0 } && Findings.Any(static f => f.Severity == ReviewSeverity.Blocking);

    [JsonIgnore]
    public int BlockingCount => Findings?.Count(static f => f.Severity == ReviewSeverity.Blocking) ?? 0;

    [JsonIgnore]
    public int WarningCount => Findings?.Count(static f => f.Severity == ReviewSeverity.Warning) ?? 0;

    [JsonIgnore]
    public string Summary => Findings is null || Findings.Length == 0
        ? "passed (0 blocking, 0 warning)"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{(HasBlocking ? "blocked" : "passed")} ({BlockingCount} blocking, {WarningCount} warning, {Findings.Length} total)");

    public string FormatForCard()
    {
        if (Findings is null || Findings.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < Findings.Length; i++)
        {
            var finding = Findings[i];
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(CultureInfo.InvariantCulture, $"- [{finding.Severity}] {finding.RuleId} ({finding.Path}:{finding.Line}): {finding.Message}");
        }

        return sb.ToString();
    }
}
