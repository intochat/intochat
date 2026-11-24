using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Fast syntax self-review of changed C# files in a worktree before the approval card is presented to the owner.
/// The syntax rules run in the Roslyn module behind <see cref="IRoslyn.ReviewSyntax"/>.
/// </summary>
internal sealed class LocalSelfReview
{
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

    public static async Task<SelfReviewReport> ReviewUnifiedDiff(IRoslyn roslyn, string diffText, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roslyn);
        if (string.IsNullOrWhiteSpace(diffText))
        {
            return SelfReviewReport.Empty;
        }

        string? currentPath = null;
        var currentNewLine = 0;
        var snippetLines = new List<string>();
        var lineMap = new List<int>();
        var addedLines = new HashSet<int>();

        var snippets = new List<(SyntaxSource Source, int[] LineMap, HashSet<int> Added)>();

        void FlushCurrentFile()
        {
            if (currentPath is not null && snippetLines.Count > 0 && addedLines.Count > 0)
            {
                snippets.Add((new SyntaxSource(currentPath, string.Join('\n', snippetLines)), [.. lineMap], [.. addedLines]));
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
                    && target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
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

                snippetLines.Add(rawLine[1..]);
                lineMap.Add(currentNewLine);
                addedLines.Add(currentNewLine);
                currentNewLine++;
            }
            else if (rawLine.StartsWith(' '))
            {
                if (currentNewLine <= 0)
                {
                    currentNewLine = 1;
                }

                snippetLines.Add(rawLine[1..]);
                lineMap.Add(currentNewLine);
                currentNewLine++;
            }
        }

        FlushCurrentFile();
        if (snippets.Count == 0)
        {
            return SelfReviewReport.Empty;
        }

        // Each snippet is reviewed under its own index label so findings map back to the file and its new-side lines.
        var labelled = snippets.Select(static (s, i) => new SyntaxSource(i.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Source.Text)).ToArray();
        var result = await roslyn.ReviewSyntax(new SyntaxReview(labelled), cancellationToken).ConfigureAwait(false);
        var findings = new List<ReviewFinding>();
        foreach (var hit in result.Findings)
        {
            if (!int.TryParse(hit.Path, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                || index < 0 || index >= snippets.Count)
            {
                continue;
            }

            var (source, map, added) = snippets[index];
            var lineIndex = hit.Line - 1;
            if (lineIndex >= 0 && lineIndex < map.Length && added.Contains(map[lineIndex]))
            {
                findings.Add(ToReviewFinding(hit) with { Path = source.Path, Line = map[lineIndex] });
            }
        }

        return new SelfReviewReport([.. findings]);
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
