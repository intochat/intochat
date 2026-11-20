using System.Globalization;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;

/// <summary>
/// Validated allowlisted repository identifier (e.g. <c>os</c> or <c>intochat/os</c>).
/// Non-defaultable with a private constructor; instantiate only through <see cref="Parse"/> or <see cref="TryParse"/>.
/// </summary>
[JsonConverter(typeof(RepositoryIdJsonConverter))]
public sealed record RepositoryId
{
    private RepositoryId(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? raw, out RepositoryId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("./", StringComparison.Ordinal)
            || raw.StartsWith(".\\", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > 128)
        {
            return false;
        }

        if (trimmed.StartsWith('/') || trimmed.EndsWith('/') || trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is ':' or '?' or '*' or '[' or '~' or '^')
            {
                return false;
            }
        }

        id = new RepositoryId(trimmed);
        return true;
    }

    public static RepositoryId Parse(string raw)
        => TryParse(raw, out var id)
            ? id!
            : throw new ArgumentException($"Invalid repository identifier '{raw}'.", nameof(raw));

    public override string ToString() => Value;

    internal sealed class RepositoryIdJsonConverter : JsonConverter<RepositoryId>
    {
        public override RepositoryId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string for RepositoryId.");
            }

            return TryParse(reader.GetString(), out var id)
                ? id
                : throw new JsonException("Invalid RepositoryId value.");
        }

        public override void Write(Utf8JsonWriter writer, RepositoryId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Validated Git branch name (e.g. <c>main</c> or <c>ino/fix-typo-1234</c>).
/// Non-defaultable with a private constructor; instantiate only through <see cref="Parse"/>, <see cref="TryParse"/>,
/// <see cref="FromRequestedBase"/>, or <see cref="CreateFeatureBranch"/>.
/// </summary>
[JsonConverter(typeof(BranchNameJsonConverter))]
public sealed partial record BranchName
{
    private BranchName(string value) => Value = value;

    public string Value { get; }

    public bool IsFeatureBranch => Value.StartsWith("ino/", StringComparison.Ordinal);

    public string RemoteRef => $"refs/heads/{Value}";

    public static bool TryParse(string? raw, out BranchName? branch)
    {
        branch = null;
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("./", StringComparison.Ordinal)
            || raw.StartsWith(".\\", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > 200)
        {
            return false;
        }

        if (trimmed.StartsWith('-') || trimmed.StartsWith('/') || trimmed.EndsWith('/') || trimmed.EndsWith('.')
            || trimmed.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains("@{", StringComparison.Ordinal)
            || trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed == "@")
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch) || ch is '\\' or '?' or '*' or '[' or '~' or '^' or ':')
            {
                return false;
            }
        }

        var segments = trimmed.Split('/');
        if (segments.Any(static s => s.Length == 0 || s.StartsWith('.') || s.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        branch = new BranchName(trimmed);
        return true;
    }

    public static BranchName Parse(string raw)
        => TryParse(raw, out var branch)
            ? branch!
            : throw new ArgumentException($"Invalid Git branch name '{raw}'.", nameof(raw));

    public static BranchName FromRequestedBase(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var clean = raw.Trim();
        if (clean.StartsWith("origin/", StringComparison.Ordinal))
        {
            clean = clean["origin/".Length..];
        }

        return Parse(clean);
    }

    public static BranchName CreateFeatureBranch(string goal, string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var safeRun = SanitizeSegment(runId);
        var shortRun = safeRun.Length <= 8 ? safeRun : safeRun[..8];
        var slug = SlugifyGoal(goal);
        return Parse($"ino/{slug}-{shortRun}");
    }

    public static BranchName CreateParallelFeatureBranch(string goal, string runId, int instanceIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfNegative(instanceIndex);
        var safeRun = SanitizeSegment(runId);
        var shortRun = safeRun.Length <= 8 ? safeRun : safeRun[..8];
        var slug = SlugifyGoal(goal);
        return Parse(string.Create(CultureInfo.InvariantCulture, $"ino/{slug}-{shortRun}-w{instanceIndex + 1}"));
    }

    internal static string SanitizeSegment(string value)
    {
        var chars = new char[value.Length];
        var len = 0;
        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                chars[len++] = char.ToLowerInvariant(ch);
            }
            else if (ch is '/' or '.' or ' ')
            {
                chars[len++] = '-';
            }
        }

        var res = CollapseHyphensRegex().Replace(new string(chars, 0, len), "-").Trim('-');
        return res.Length == 0 ? "run" : (res.Length <= 40 ? res : res[..40].TrimEnd('-'));
    }

    internal static string SlugifyGoal(string goal)
    {
        var chars = new char[goal.Length];
        for (var i = 0; i < goal.Length; i++)
        {
            var ch = goal[i];
            chars[i] = char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
        }

        var slug = CollapseHyphensRegex().Replace(new string(chars), "-").Trim('-');
        if (slug.Length == 0)
        {
            return "change";
        }

        return slug.Length <= 28 ? slug : slug[..28].TrimEnd('-');
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex CollapseHyphensRegex();

    internal sealed class BranchNameJsonConverter : JsonConverter<BranchName>
    {
        public override BranchName? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string for BranchName.");
            }

            return TryParse(reader.GetString(), out var branch)
                ? branch
                : throw new JsonException("Invalid BranchName value.");
        }

        public override void Write(Utf8JsonWriter writer, BranchName value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Validated 40-character lowercase hexadecimal Git commit SHA.
/// Non-defaultable with a private constructor; instantiate only through <see cref="Parse"/> or <see cref="TryParse"/>.
/// </summary>
[JsonConverter(typeof(CommitShaJsonConverter))]
public sealed record CommitSha
{
    private CommitSha(string value) => Value = value;

    public string Value { get; }

    public string Short => Value[..8];

    public static bool TryParse(string? raw, out CommitSha? sha)
    {
        sha = null;
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("./", StringComparison.Ordinal)
            || raw.StartsWith(".\\", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length != 40)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiHexDigit(ch))
            {
                return false;
            }
        }

        sha = new CommitSha(trimmed.ToLowerInvariant());
        return true;
    }

    public static CommitSha Parse(string raw)
        => TryParse(raw, out var sha)
            ? sha!
            : throw new ArgumentException($"Invalid 40-character commit SHA '{raw}'.", nameof(raw));

    public override string ToString() => Value;

    internal sealed class CommitShaJsonConverter : JsonConverter<CommitSha>
    {
        public override CommitSha? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string for CommitSha.");
            }

            return TryParse(reader.GetString(), out var sha)
                ? sha
                : throw new JsonException("Invalid CommitSha value.");
        }

        public override void Write(Utf8JsonWriter writer, CommitSha value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Validated forward-slash normalized repository-relative file path without traversal or rooting.
/// Non-defaultable with a private constructor; instantiate only through <see cref="Parse"/> or <see cref="TryParse"/>.
/// </summary>
[JsonConverter(typeof(RepoPathJsonConverter))]
public sealed record RepoPath
{
    private RepoPath(string value) => Value = value;

    public string Value { get; }

    public IReadOnlyList<string> Segments => Value.Split('/');

    public string FileName => Segments[^1];

    public string Extension => Path.GetExtension(FileName);

    public bool IsCSharp => string.Equals(Extension, ".cs", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? raw, out RepoPath? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(raw)
            || raw.StartsWith("./", StringComparison.Ordinal)
            || raw.StartsWith(".\\", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        var trimmed = raw.Trim();
        var normalized = trimmed.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized) || !SafeRelativePath.TryParse(trimmed, out var safe))
        {
            return false;
        }

        path = new RepoPath(safe.Value);
        return true;
    }

    public static RepoPath Parse(string raw)
        => TryParse(raw, out var path)
            ? path!
            : throw new ArgumentException($"Invalid or escaping repository path '{raw}'.", nameof(raw));

    public string ResolveUnder(string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        var fullRoot = Path.GetFullPath(treeRoot);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, Value.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(fullRoot, comparison))
        {
            throw new SecurityException($"Path gate rejected path escaping worktree root: '{Value}'.");
        }

        return candidate;
    }

    public override string ToString() => Value;

    internal sealed class RepoPathJsonConverter : JsonConverter<RepoPath>
    {
        public override RepoPath? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected string for RepoPath.");
            }

            return TryParse(reader.GetString(), out var path)
                ? path
                : throw new JsonException("Invalid RepoPath value.");
        }

        public override void Write(Utf8JsonWriter writer, RepoPath value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// Verified single-commit output produced after passing Triple Git Lock, <see cref="ChangePolicy"/>, and whitespace formatting gates.
/// Non-defaultable with a private constructor; instantiate only through <see cref="Create"/> or <see cref="FromDiff"/>.
/// </summary>
[JsonConverter(typeof(ChangeSetJsonConverter))]
public sealed record ChangeSet
{
    private ChangeSet(
        CommitSha commit,
        RepoPath[] changedFiles,
        string hostRenderedDiff,
        int additions,
        int deletions)
    {
        Commit = commit;
        ChangedFiles = changedFiles;
        HostRenderedDiff = hostRenderedDiff;
        Additions = additions;
        Deletions = deletions;
    }

    public CommitSha Commit { get; }

    public RepoPath[] ChangedFiles { get; }

    public string HostRenderedDiff { get; }

    public int Additions { get; }

    public int Deletions { get; }

    public bool Equals(ChangeSet? other)
        => other is not null
        && Commit.Equals(other.Commit)
        && ChangedFiles.SequenceEqual(other.ChangedFiles)
        && string.Equals(HostRenderedDiff, other.HostRenderedDiff, StringComparison.Ordinal)
        && Additions == other.Additions
        && Deletions == other.Deletions;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Commit);
        foreach (var file in ChangedFiles)
        {
            hash.Add(file);
        }

        hash.Add(HostRenderedDiff, StringComparer.Ordinal);
        hash.Add(Additions);
        hash.Add(Deletions);
        return hash.ToHashCode();
    }

    public static ChangeSet Create(
        CommitSha commit,
        IEnumerable<RepoPath> changedFiles,
        string hostRenderedDiff,
        int additions,
        int deletions)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(hostRenderedDiff);
        ArgumentOutOfRangeException.ThrowIfNegative(additions);
        ArgumentOutOfRangeException.ThrowIfNegative(deletions);

        var normalized = changedFiles
            .Distinct()
            .OrderBy(static p => p.Value, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A ChangeSet must include at least one changed file.", nameof(changedFiles));
        }

        return new ChangeSet(commit, normalized, hostRenderedDiff, additions, deletions);
    }

    public static ChangeSet FromDiff(
        CommitSha commit,
        IEnumerable<RepoPath> changedFiles,
        string hostRenderedDiff)
    {
        ArgumentNullException.ThrowIfNull(hostRenderedDiff);

        var additions = 0;
        var deletions = 0;
        foreach (var line in hostRenderedDiff.Split('\n'))
        {
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('+'))
            {
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                deletions++;
            }
        }

        return Create(commit, changedFiles, hostRenderedDiff, additions, deletions);
    }

    internal sealed class ChangeSetJsonConverter : JsonConverter<ChangeSet>
    {
        private sealed record WireShape(
            CommitSha? Commit,
            RepoPath[]? ChangedFiles,
            string? HostRenderedDiff,
            int Additions,
            int Deletions);

        public override ChangeSet? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            var wire = JsonSerializer.Deserialize<WireShape>(ref reader, options)
                ?? throw new JsonException("ChangeSet JSON object was null.");
            if (wire.Commit is null || wire.ChangedFiles is null || wire.HostRenderedDiff is null)
            {
                throw new JsonException("ChangeSet requires commit, changedFiles, and hostRenderedDiff.");
            }

            return Create(wire.Commit, wire.ChangedFiles, wire.HostRenderedDiff, wire.Additions, wire.Deletions);
        }

        public override void Write(Utf8JsonWriter writer, ChangeSet value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(
                writer,
                new WireShape(value.Commit, value.ChangedFiles, value.HostRenderedDiff, value.Additions, value.Deletions),
                options);
    }
}

internal static partial class GitHubRemoteParser
{
    internal static string? ParseOwnerRepo(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var match = GitHubUrlRegex().Match(remoteUrl.Trim());
        if (!match.Success)
        {
            return null;
        }

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return $"{owner}/{repo}";
    }

    [GeneratedRegex(@"(?:github\.com[:/])(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubUrlRegex();
}
