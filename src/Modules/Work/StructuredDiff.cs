using System.Text;

namespace IntoChat.OS.Modules.Work;

public enum DiffKind
{
    Modified,
    Added,
    Deleted,
}

public enum DiffLineKind
{
    Context,
    Addition,
    Deletion,
}

public sealed record DiffLine(
    DiffLineKind Kind,
    string Content,
    int? OldLineNumber = null,
    int? NewLineNumber = null);

public sealed record DiffHunk(
    string Header,
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    IReadOnlyList<DiffLine> Lines);

public sealed record FileDiff(
    string Path,
    string OldPath,
    DiffKind Kind,
    IReadOnlyList<DiffHunk> Hunks,
    int Additions,
    int Deletions,
    string RawDiff);

public sealed record StructuredDiff(
    IReadOnlyList<FileDiff> Files,
    int TotalAdditions,
    int TotalDeletions,
    string RawText)
{
    public static readonly StructuredDiff Empty = new([], 0, 0, string.Empty);
}

public static class UnifiedDiffGenerator
{
    public static StructuredDiff Generate(
        IReadOnlyDictionary<string, string> baseFiles,
        IReadOnlyDictionary<string, string> modifiedFiles)
    {
        ArgumentNullException.ThrowIfNull(baseFiles);
        ArgumentNullException.ThrowIfNull(modifiedFiles);

        var allPaths = new SortedSet<string>(baseFiles.Keys.Concat(modifiedFiles.Keys), StringComparer.Ordinal);
        var fileDiffs = new List<FileDiff>();
        var totalAdd = 0;
        var totalDel = 0;
        var rawTextBuilder = new StringBuilder();

        foreach (var path in allPaths)
        {
            var inBase = baseFiles.TryGetValue(path, out var baseContent);
            var inMod = modifiedFiles.TryGetValue(path, out var modContent);

            if (inBase && !inMod)
            {
                var oldLines = SplitLines(baseContent!);
                var diffLines = oldLines.Select((l, i) => new DiffLine(DiffLineKind.Deletion, l, i + 1, null)).ToArray();
                var header = $"@@ -1,{Math.Max(1, oldLines.Length)} +0,0 @@";
                var hunk = new DiffHunk(header, 1, oldLines.Length, 0, 0, diffLines);
                var rawBuilder = new StringBuilder();
                rawBuilder.AppendLine($"--- a/{path}");
                rawBuilder.AppendLine("+++ /dev/null");
                rawBuilder.AppendLine(header);
                foreach (var line in oldLines) { rawBuilder.AppendLine($"-{line}"); }

                var raw = rawBuilder.ToString();
                totalDel += oldLines.Length;
                fileDiffs.Add(new FileDiff(path, path, DiffKind.Deleted, [hunk], 0, oldLines.Length, raw));
                rawTextBuilder.Append(raw);
            }
            else if (!inBase && inMod)
            {
                var newLines = SplitLines(modContent!);
                var diffLines = newLines.Select((l, i) => new DiffLine(DiffLineKind.Addition, l, null, i + 1)).ToArray();
                var header = $"@@ -0,0 +1,{Math.Max(1, newLines.Length)} @@";
                var hunk = new DiffHunk(header, 0, 0, 1, newLines.Length, diffLines);
                var rawBuilder = new StringBuilder();
                rawBuilder.AppendLine("--- /dev/null");
                rawBuilder.AppendLine($"+++ b/{path}");
                rawBuilder.AppendLine(header);
                foreach (var line in newLines) { rawBuilder.AppendLine($"+{line}"); }

                var raw = rawBuilder.ToString();
                totalAdd += newLines.Length;
                fileDiffs.Add(new FileDiff(path, path, DiffKind.Added, [hunk], newLines.Length, 0, raw));
                rawTextBuilder.Append(raw);
            }
            else if (inBase && inMod)
            {
                if (string.Equals(baseContent, modContent, StringComparison.Ordinal))
                {
                    continue;
                }

                var oldLines = SplitLines(baseContent!);
                var newLines = SplitLines(modContent!);
                var (hunks, addCount, delCount, fileRaw) = ComputeHunks(path, oldLines, newLines);

                if (hunks.Count > 0)
                {
                    totalAdd += addCount;
                    totalDel += delCount;
                    fileDiffs.Add(new FileDiff(path, path, DiffKind.Modified, hunks, addCount, delCount, fileRaw));
                    rawTextBuilder.Append(fileRaw);
                }
            }
        }

        return new StructuredDiff(fileDiffs, totalAdd, totalDel, rawTextBuilder.ToString());
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static (List<DiffHunk> Hunks, int Additions, int Deletions, string Raw) ComputeHunks(
        string path,
        string[] oldLines,
        string[] newLines)
    {
        var diffOps = ComputeLcsDiff(oldLines, newLines);
        var hunks = new List<DiffHunk>();
        var additions = 0;
        var deletions = 0;
        var rawBuilder = new StringBuilder();

        rawBuilder.AppendLine($"--- a/{path}");
        rawBuilder.AppendLine($"+++ b/{path}");

        var diffLines = new List<DiffLine>();
        var oldLineNum = 1;
        var newLineNum = 1;

        foreach (var (kind, line) in diffOps)
        {
            switch (kind)
            {
                case DiffLineKind.Context:
                    diffLines.Add(new DiffLine(DiffLineKind.Context, line, oldLineNum++, newLineNum++));
                    break;
                case DiffLineKind.Deletion:
                    diffLines.Add(new DiffLine(DiffLineKind.Deletion, line, oldLineNum++, null));
                    deletions++;
                    break;
                case DiffLineKind.Addition:
                    diffLines.Add(new DiffLine(DiffLineKind.Addition, line, null, newLineNum++));
                    additions++;
                    break;
            }
        }

        if (diffLines.Count > 0)
        {
            var header = $"@@ -1,{Math.Max(1, oldLines.Length)} +1,{Math.Max(1, newLines.Length)} @@";
            hunks.Add(new DiffHunk(header, 1, oldLines.Length, 1, newLines.Length, diffLines));
            rawBuilder.AppendLine(header);
            foreach (var dl in diffLines)
            {
                var prefix = dl.Kind switch
                {
                    DiffLineKind.Addition => "+",
                    DiffLineKind.Deletion => "-",
                    _ => " ",
                };
                rawBuilder.AppendLine($"{prefix}{dl.Content}");
            }
        }

        return (hunks, additions, deletions, rawBuilder.ToString());
    }

    private static List<(DiffLineKind Kind, string Line)> ComputeLcsDiff(string[] a, string[] b)
    {
        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                dp[i + 1, j + 1] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? dp[i, j] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<(DiffLineKind, string)>();
        var x = n;
        var y = m;

        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && string.Equals(a[x - 1], b[y - 1], StringComparison.Ordinal))
            {
                result.Add((DiffLineKind.Context, a[x - 1]));
                x--;
                y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                result.Add((DiffLineKind.Addition, b[y - 1]));
                y--;
            }
            else if (x > 0 && (y == 0 || dp[x, y - 1] < dp[x - 1, y]))
            {
                result.Add((DiffLineKind.Deletion, a[x - 1]));
                x--;
            }
        }

        result.Reverse();
        return result;
    }
}
