using System.Globalization;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Fast syntax, boundary, and secret self-review of changed files or unified diffs before the approval card is presented to the owner.
/// Syntax rules run in the Roslyn module behind <see cref="IRoslyn.ReviewSyntax"/>; boundary and credential audits run deterministically on every diff.
/// </summary>
internal sealed partial class LocalSelfReview
{
    public const string FrameworkIsolationRuleId = "ARCH-FrameworkIsolation";
    public const string HostBoundaryRuleId = "ARCH-HostBoundary";
    public const string SecretRuleId = "SEC-Secret";
    public const string SwallowRuleId = "HR-Swallow";

    [GeneratedRegex(@"\bIntoChat\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntoChatIdentifierRegex();

    [GeneratedRegex(@"(?:IntoChat\.OS\.(?:Modules|Kernel)|ProjectReference\s+Include\s*=\s*""[^""]*(?:Modules|IntoChat\.OS\.Kernel)[^""]*"")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostBoundaryReferenceRegex();

    [GeneratedRegex(@"-----BEGIN\s+[A-Z0-9 ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyHeaderRegex();

    [GeneratedRegex(@"\b(?:(?:ghp|gho|ghu|ghs)_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-(?:proj-|ant-|live-|test-)?[A-Za-z0-9_\-]{20,}|AKIA[0-9A-Z]{16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPrefixRegex();

    [GeneratedRegex(@"(?:Password|Pwd|AccountKey|SharedAccessKey)\s*=\s*([^\s;""'<>]{8,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecretRegex();

    [GeneratedRegex(@"\b(?:api_?key|client_?secret|access_?token|auth_?token|private_?key|secret_?key)\b\s*[:=]\s*[""']([A-Za-z0-9+/=_\-]{16,})[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HardcodedSecretLiteralRegex();

    [GeneratedRegex(@"\bcatch\b(?:\s*\([^)]*\))?\s*\{\s*\}", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyCatchRegex();

    public static async Task<SelfReviewReport> ReviewWorktree(
        IRoslyn roslyn,
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roslyn);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var sources = new List<SyntaxSource>();

        foreach (var rawPath in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(rawPath)
                || !rawPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalizedPath = rawPath.Replace('\\', '/');
            var fullPath = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(worktreePath, rawPath);

            if (!File.Exists(fullPath))
            {
                continue;
            }

            var sourceText = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            sources.Add(new SyntaxSource(normalizedPath, sourceText));
        }

        if (sources.Count == 0)
        {
            return SelfReviewReport.Empty;
        }

        var result = await roslyn.ReviewSyntax(new SyntaxReview(sources), cancellationToken).ConfigureAwait(false);
        return new SelfReviewReport([.. result.Findings.Select(ToReviewFinding)]);
    }

    public static Task<SelfReviewReport> ReviewUnifiedDiff(IRoslyn roslyn, string diffText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roslyn);
        return ReviewUnifiedDiffCoreAsync(roslyn, diffText, cancellationToken);
    }

    public static Task<SelfReviewReport> ReviewUnifiedDiffOrFallback(IRoslyn? roslyn, string diffText, CancellationToken cancellationToken = default)
        => ReviewUnifiedDiffCoreAsync(roslyn, diffText, cancellationToken);

    private static async Task<SelfReviewReport> ReviewUnifiedDiffCoreAsync(
        IRoslyn? roslyn,
        string diffText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return SelfReviewReport.Empty;
        }

        string? currentPath = null;
        var currentNewLine = 0;
        var snippetLines = new List<string>();
        var lineMap = new List<int>();
        var addedLines = new HashSet<int>();

        var csSnippets = new List<(SyntaxSource Source, int[] LineMap, HashSet<int> Added)>();
        var staticFindings = new List<ReviewFinding>();

        void FlushCurrentFile()
        {
            if (currentPath is not null
                && currentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && snippetLines.Count > 0
                && addedLines.Count > 0)
            {
                csSnippets.Add((new SyntaxSource(currentPath, string.Join('\n', snippetLines)), [.. lineMap], [.. addedLines]));
            }

            snippetLines.Clear();
            lineMap.Clear();
            addedLines.Clear();
        }

        foreach (var rawLine in diffText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (rawLine.StartsWith("+++ ", StringComparison.Ordinal))
            {
                FlushCurrentFile();
                var target = rawLine[4..].Trim();
                if (target.StartsWith("b/", StringComparison.Ordinal))
                {
                    target = target[2..];
                }

                currentPath = !string.Equals(target, "/dev/null", StringComparison.Ordinal)
                    ? target.Replace('\\', '/')
                    : null;
                currentNewLine = 0;
                continue;
            }

            if (currentPath is null)
            {
                continue;
            }

            if (rawLine.StartsWith("@@ ", StringComparison.Ordinal))
            {
                currentNewLine = ParseHunkNewStart(rawLine);
                continue;
            }

            if (rawLine.StartsWith("diff --git ", StringComparison.Ordinal)
                || rawLine.StartsWith("--- ", StringComparison.Ordinal)
                || rawLine.StartsWith("\\ ", StringComparison.Ordinal))
            {
                continue;
            }

            if (rawLine.StartsWith('+'))
            {
                if (currentNewLine <= 0)
                {
                    currentNewLine = 1;
                }

                var addedContent = rawLine[1..];
                AuditAddedLine(currentPath, currentNewLine, addedContent, staticFindings);

                if (currentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    snippetLines.Add(addedContent);
                    lineMap.Add(currentNewLine);
                    addedLines.Add(currentNewLine);
                }

                currentNewLine++;
            }
            else if (rawLine.StartsWith(' '))
            {
                if (currentNewLine <= 0)
                {
                    currentNewLine = 1;
                }

                if (currentPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    snippetLines.Add(rawLine[1..]);
                    lineMap.Add(currentNewLine);
                }

                currentNewLine++;
            }
        }

        FlushCurrentFile();

        var findings = new List<ReviewFinding>(staticFindings);
        if (csSnippets.Count > 0)
        {
            if (roslyn is not null)
            {
                // Each snippet is reviewed under its own index label so findings map back to the file and its new-side lines.
                var labelled = csSnippets.Select(static (s, i) => new SyntaxSource(i.ToString(CultureInfo.InvariantCulture), s.Source.Text)).ToArray();
                var result = await roslyn.ReviewSyntax(new SyntaxReview(labelled), cancellationToken).ConfigureAwait(false);
                foreach (var hit in result.Findings)
                {
                    if (!int.TryParse(hit.Path, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        || index < 0 || index >= csSnippets.Count)
                    {
                        continue;
                    }

                    var (source, map, added) = csSnippets[index];
                    var lineIndex = hit.Line - 1;
                    if (lineIndex >= 0 && lineIndex < map.Length && added.Contains(map[lineIndex]))
                    {
                        findings.Add(ToReviewFinding(hit) with { Path = source.Path, Line = map[lineIndex] });
                    }
                }
            }
            else
            {
                foreach (var (source, map, added) in csSnippets)
                {
                    AuditSwallowedCatchesFallback(source.Path, source.Text, map, added, findings);
                }
            }
        }

        if (findings.Count == 0)
        {
            return SelfReviewReport.Empty;
        }

        var deduplicated = findings
            .DistinctBy(static f => (f.RuleId, f.Path, f.Line))
            .ToArray();
        return new SelfReviewReport(deduplicated);
    }

    private static void AuditAddedLine(string path, int lineNumber, string lineText, List<ReviewFinding> findings)
    {
        if (path.StartsWith("framework/", StringComparison.OrdinalIgnoreCase)
            && IntoChatIdentifierRegex().IsMatch(lineText))
        {
            findings.Add(new ReviewFinding(
                FrameworkIsolationRuleId,
                ReviewFindingSource.ArchitectureFact,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "FrameworkBoundaryFacts violation: code under framework/ must never reference IntoChat."));
        }

        if (path.StartsWith("src/IntoChat.OS.Host/", StringComparison.OrdinalIgnoreCase)
            && HostBoundaryReferenceRegex().IsMatch(lineText))
        {
            findings.Add(new ReviewFinding(
                HostBoundaryRuleId,
                ReviewFindingSource.ArchitectureFact,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "Host boundary violation: src/IntoChat.OS.Host must never reference IntoChat.OS.Modules or IntoChat.OS.Kernel."));
        }

        if (PrivateKeyHeaderRegex().IsMatch(lineText))
        {
            findings.Add(new ReviewFinding(
                SecretRuleId,
                ReviewFindingSource.Analyzer,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "Secret/credential violation: private key material detected in diff."));
            return;
        }

        if (KnownTokenPrefixRegex().IsMatch(lineText))
        {
            findings.Add(new ReviewFinding(
                SecretRuleId,
                ReviewFindingSource.Analyzer,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "Secret/credential violation: hardcoded API token or cloud access key detected in diff."));
            return;
        }

        var connMatch = ConnectionStringSecretRegex().Match(lineText);
        if (connMatch.Success && !IsPlaceholderSecret(connMatch.Groups[1].Value))
        {
            findings.Add(new ReviewFinding(
                SecretRuleId,
                ReviewFindingSource.Analyzer,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "Secret/credential violation: hardcoded password or access key in connection string."));
            return;
        }

        var literalMatch = HardcodedSecretLiteralRegex().Match(lineText);
        if (literalMatch.Success && !IsPlaceholderSecret(literalMatch.Groups[1].Value))
        {
            findings.Add(new ReviewFinding(
                SecretRuleId,
                ReviewFindingSource.Analyzer,
                path,
                lineNumber,
                ReviewSeverity.Blocking,
                "Secret/credential violation: hardcoded secret literal detected in diff."));
        }
    }

    private static bool IsPlaceholderSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var lower = value.Trim().ToLowerInvariant();
        return lower.Contains("placeholder", StringComparison.Ordinal)
            || lower.Contains("changeme", StringComparison.Ordinal)
            || lower.Contains("example", StringComparison.Ordinal)
            || lower.Contains("your-", StringComparison.Ordinal)
            || lower.Contains("your_", StringComparison.Ordinal)
            || lower.Contains("dummy", StringComparison.Ordinal)
            || lower.Contains("redact", StringComparison.Ordinal)
            || lower.All(static c => c is '*' or 'x' or '0');
    }

    private static void AuditSwallowedCatchesFallback(
        string path,
        string snippetText,
        int[] lineMap,
        HashSet<int> addedLines,
        List<ReviewFinding> findings)
    {
        foreach (Match match in EmptyCatchRegex().Matches(snippetText))
        {
            var snippetLineIndex = 0;
            for (var i = 0; i < match.Index && i < snippetText.Length; i++)
            {
                if (snippetText[i] == '\n')
                {
                    snippetLineIndex++;
                }
            }

            if (snippetLineIndex >= 0 && snippetLineIndex < lineMap.Length && addedLines.Contains(lineMap[snippetLineIndex]))
            {
                findings.Add(new ReviewFinding(
                    SwallowRuleId,
                    ReviewFindingSource.Analyzer,
                    path,
                    lineMap[snippetLineIndex],
                    ReviewSeverity.Blocking,
                    "Empty or silent catch block swallows exception (HR-Swallow / DB0022)."));
            }
        }
    }

    private static int ParseHunkNewStart(string header)
    {
        var plusIndex = header.IndexOf('+', StringComparison.Ordinal);
        if (plusIndex < 0)
        {
            return 1;
        }

        var start = plusIndex + 1;
        var end = start;
        while (end < header.Length && char.IsDigit(header[end]))
        {
            end++;
        }

        return end > start && int.TryParse(header[start..end], out var parsed) && parsed > 0
            ? parsed
            : 1;
    }

    private static ReviewFinding ToReviewFinding(SyntaxFinding hit) => new(
        hit.RuleId,
        hit.Kind == SyntaxFindingKind.Analyzer ? ReviewFindingSource.Analyzer : ReviewFindingSource.ArchitectureFact,
        hit.Path,
        hit.Line,
        hit.Blocking ? ReviewSeverity.Blocking : ReviewSeverity.Warning,
        hit.Message);
}
