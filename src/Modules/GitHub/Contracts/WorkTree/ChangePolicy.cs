using System.Collections.Frozen;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Single shared domain policy governing allowed Feature-to-PR file paths, extensions, file-count limits,
/// disk reparse confinement, prompt rendering, and commit message sanitization.
/// </summary>
public sealed partial record ChangePolicy
{
    public const int DefaultMaxChangedFiles = 5;

    private static readonly string[] DefaultAllowedExtensions =
    [
        ".cs",
        ".md",
        ".dart",
        ".txt",
        ".xaml",
        ".svg",
    ];

    private static readonly string[] DefaultBlockedFileNames =
    [
        "claude.md",
        "agents.md",
        "skill.md",
        "copilot-instructions.md",
    ];

    public static ChangePolicy Default { get; } = new();

    public ChangePolicy(
        int maxChangedFiles = DefaultMaxChangedFiles,
        IEnumerable<string>? allowedExtensions = null,
        IEnumerable<string>? blockedFileNames = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxChangedFiles, 0);
        MaxChangedFiles = maxChangedFiles;

        var extensions = (allowedExtensions ?? DefaultAllowedExtensions)
            .Select(static e => e.Trim())
            .Where(static e => e.Length > 0)
            .Select(static e => e.StartsWith('.') ? e : "." + e);
        AllowedExtensions = extensions.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        var blocked = (blockedFileNames ?? DefaultBlockedFileNames)
            .Select(static f => f.Trim())
            .Where(static f => f.Length > 0);
        BlockedFileNames = blocked.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public int MaxChangedFiles { get; }

    public FrozenSet<string> AllowedExtensions { get; }

    public FrozenSet<string> BlockedFileNames { get; }

    /// <summary>
    /// Evaluates a validated <see cref="RepoPath"/> against dotfile, blocked instruction file, and allowed extension rules.
    /// Returns <see langword="null"/> when allowed, or a human-readable refusal reason.
    /// </summary>
    public string? Check(RepoPath path) => CheckAccess(path, requireWriteAllowed: true, allowDirectory: false);

    private string? CheckAccess(RepoPath path, bool requireWriteAllowed, bool allowDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);

        foreach (var seg in path.Segments)
        {
            if (seg.StartsWith('.'))
            {
                return $"Path gate rejected dotfile or hidden directory path '{path.Value}'.";
            }
        }

        if (!allowDirectory && BlockedFileNames.Contains(path.FileName))
        {
            return $"Path gate rejected agent instruction file '{path.Value}'.";
        }

        if (requireWriteAllowed)
        {
            var ext = path.Extension;
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            {
                var sorted = string.Join(", ", AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
                return $"Path gate rejected disallowed file extension '{ext}' on '{path.Value}'. Allowed: {sorted}.";
            }
        }

        return null;
    }

    /// <summary>
    /// Evaluates a list of <see cref="RepoPath"/> changes against non-emptiness, file-count budget, and per-path rules.
    /// </summary>
    public string? Check(IReadOnlyList<RepoPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return "Coder produced zero file modifications in the worktree.";
        }

        if (paths.Count > MaxChangedFiles)
        {
            return $"Path gate rejected change: {paths.Count} files modified, exceeding the maximum of {MaxChangedFiles} files.";
        }

        foreach (var path in paths)
        {
            if (Check(path) is { } refusal)
            {
                return refusal;
            }
        }

        return null;
    }

    /// <summary>Evaluates a <see cref="ChangeSet"/> against this policy.</summary>
    public string? Check(ChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return Check(changeSet.ChangedFiles);
    }

    /// <summary>Evaluates a raw path string for sandboxed coder tool consumption.</summary>
    public string? CheckRawPath(string rawPath)
        => RepoPath.TryParse(rawPath, out var parsed)
            ? Check(parsed!)
            : $"Path gate rejected invalid or traversal path '{rawPath}'.";

    /// <summary>
    /// Validates <paramref name="rawPath"/> against traversal, dotfiles, blocked instruction files,
    /// optional extension write policy (<paramref name="requireWriteAllowed"/>), root confinement, and symlinks/reparse points.
    /// Returns the normalized repository-relative path and confined full disk path, or throws <see cref="SecurityException"/>.
    /// </summary>
    public (string RelativePath, string FullPath) ResolveConfinedPath(
        string treeRoot,
        string rawPath,
        bool requireWriteAllowed,
        bool allowDirectory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        var normalizedRoot = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (allowDirectory && string.Equals(rawPath?.Trim(), ".", StringComparison.Ordinal))
        {
            return ("", normalizedRoot);
        }

        var (repoPath, candidateFull) = ResolveAndVerifyRepoPath(normalizedRoot, rawPath, requireWriteAllowed, allowDirectory);
        return (repoPath.Value, candidateFull);
    }

    /// <summary>
    /// Validates raw paths, enforces policy rules, verifies root confinement on disk, and rejects symlinks/reparse points.
    /// Throws <see cref="SecurityException"/> on any violation.
    /// </summary>
    public RepoPath[] Enforce(string treeRoot, IReadOnlyList<string> rawPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        ArgumentNullException.ThrowIfNull(rawPaths);

        if (rawPaths.Count > MaxChangedFiles)
        {
            throw new SecurityException(
                $"Path gate rejected change: {rawPaths.Count} files modified, exceeding the maximum of {MaxChangedFiles} files.");
        }

        var parsed = new List<RepoPath>(rawPaths.Count);
        foreach (var raw in rawPaths)
        {
            var (repoPath, _) = ResolveAndVerifyRepoPath(treeRoot, raw, requireWriteAllowed: true, allowDirectory: false);
            parsed.Add(repoPath);
        }

        return [.. parsed.Distinct().OrderBy(static p => p.Value, StringComparer.Ordinal)];
    }

    private (RepoPath Path, string FullPath) ResolveAndVerifyRepoPath(
        string treeRoot,
        string? rawPath,
        bool requireWriteAllowed,
        bool allowDirectory)
    {
        if (!RepoPath.TryParse(rawPath, out var repoPath))
        {
            throw new SecurityException($"Path gate rejected invalid, rooted, or traversal path '{rawPath}'.");
        }

        if (CheckAccess(repoPath!, requireWriteAllowed, allowDirectory) is { } refusal)
        {
            throw new SecurityException(refusal);
        }

        var candidateFull = repoPath!.ResolveUnder(treeRoot);
        EnsureNoReparsePointsInHierarchy(treeRoot, candidateFull);
        return (repoPath, candidateFull);
    }

    /// <summary>
    /// Renders the exact active rules as prompt text so coder instructions and the host gate never drift.
    /// </summary>
    public string Describe()
    {
        var extensions = string.Join(", ", AllowedExtensions.Order(StringComparer.OrdinalIgnoreCase));
        var blocked = string.Join(", ", BlockedFileNames.Order(StringComparer.OrdinalIgnoreCase));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"1. Modify at most {MaxChangedFiles} files total, and ONLY touch files directly needed for the goal.\n"
            + $"2. Allowed extensions ONLY: {extensions}. NEVER touch dotfiles (.git, .github, .claude, etc.) or instruction files ({blocked}), or use path traversal/symlinks.");
    }

    /// <summary>
    /// Strips GitHub skip-CI directives and control/bidi characters from model-authored commit summaries and titles.
    /// </summary>
    public static string SanitizeCommitText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var withoutTokens = SkipCiTokenRegex().Replace(text.Replace("\r\n", "\n", StringComparison.Ordinal), string.Empty);
        var lines = new List<string>();
        foreach (var rawLine in withoutTokens.Split('\n'))
        {
            var sb = new StringBuilder(rawLine.Length);
            foreach (var ch in rawLine)
            {
                if (ch == '\t')
                {
                    sb.Append(ch);
                    continue;
                }

                if (char.IsControl(ch) || IsDisallowedFormattingChar(ch))
                {
                    continue;
                }

                sb.Append(ch);
            }

            var collapsed = MultiSpaceRegex().Replace(sb.ToString(), " ").Trim();
            if (collapsed.Length > 0)
            {
                lines.Add(collapsed);
            }
        }

        return string.Join('\n', lines).Trim();
    }

    /// <summary>Builds a sanitized commit message from user goal and optional model summary.</summary>
    public static string BuildCommitMessage(string goal, string? coderSummary)
    {
        var cleanGoal = SanitizeCommitText(goal);
        var firstLine = cleanGoal
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Automated change";
        if (firstLine.Length > 72)
        {
            firstLine = firstLine[..72].TrimEnd() + "...";
        }

        var title = firstLine.Contains(':', StringComparison.Ordinal) ? firstLine : $"feat(ino): {firstLine}";
        var cleanSummary = SanitizeCommitText(coderSummary);
        return string.IsNullOrWhiteSpace(cleanSummary) ? title : $"{title}\n\n{cleanSummary}";
    }

    /// <summary>Builds a sanitized PR title from user goal.</summary>
    public static string BuildPrTitle(string goal)
    {
        var cleanGoal = SanitizeCommitText(goal);
        var firstLine = cleanGoal
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Automated change";
        return firstLine.Length <= 80 ? firstLine : firstLine[..80].TrimEnd() + "...";
    }

    private static bool IsDisallowedFormattingChar(char ch)
        => char.GetUnicodeCategory(ch) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            || ch is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    private static void EnsureNoReparsePointsInHierarchy(string treeRoot, string targetFullPath)
    {
        var stopAt = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = targetFullPath;
        while (!string.IsNullOrEmpty(current) && !PathsEqual(current, stopAt))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var info = new FileInfo(current);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    throw new SecurityException($"Path gate rejected symlink or reparse point at '{current}'.");
                }
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"\[\s*(?:skip\s+ci|ci\s+skip|no\s+ci|skip\s+actions|actions\s+skip)\s*\]|\bskip-checks\s*:\s*(?:true|false)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkipCiTokenRegex();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();
}
