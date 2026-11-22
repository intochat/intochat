using System.Globalization;
using System.Text;

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

public sealed record ReviewFinding(
    string RuleId,
    ReviewFindingSource Source,
    string Path,
    int Line,
    ReviewSeverity Severity,
    string Message);

public sealed record SelfReviewReport(ReviewFinding[] Findings)
{
    public static SelfReviewReport Empty { get; } = new([]);

    public bool HasBlocking => Findings is { Length: > 0 } && Findings.Any(static f => f.Severity == ReviewSeverity.Blocking);

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
