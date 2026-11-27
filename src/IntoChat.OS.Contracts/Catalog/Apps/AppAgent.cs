using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Memory isolation scope for a custom specialist bot (<see cref="AppAgent"/>).
/// </summary>
public enum MemoryScope
{
    /// <summary>No durable memory recall or persistence.</summary>
    None = 0,

    /// <summary>
    /// Durable memory strictly confined to <c>memory/bots/{appId}</c> with cross-bot and host-secret isolation.
    /// </summary>
    IsolatedBotScope = 1,
}

/// <summary>
/// Validation and normalization rules for custom specialist bot handles (e.g. <c>@reviewer</c>, <c>@sql-analyst</c>).
/// </summary>
public static partial class AppAgentHandle
{
    public const string ReservedInoHandle = "@ino";
    public const int MinSlugLength = 2;
    public const int MaxSlugLength = 32;

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex HandleSlugPattern();

    /// <summary>
    /// Normalizes a raw handle (with or without leading <c>@</c>) into canonical lowercase <c>@slug</c> form.
    /// Rejects invalid syntax and the reserved <c>@ino</c> handle.
    /// </summary>
    public static string Normalize(string? rawHandle)
    {
        if (!TryNormalize(rawHandle, out var normalized, out var error))
        {
            throw new InvalidDataException(error);
        }

        return normalized;
    }

    /// <summary>
    /// Attempts to normalize a raw specialist handle, returning a user-actionable error when invalid.
    /// </summary>
    public static bool TryNormalize(string? rawHandle, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(rawHandle))
        {
            error = "Specialist handle must be non-empty (for example '@reviewer' or '@sql-analyst').";
            return false;
        }

        var trimmed = rawHandle.Trim();
        var slug = trimmed.StartsWith('@') ? trimmed[1..] : trimmed;
        slug = slug.ToLowerInvariant();

        if (slug.Length < MinSlugLength || slug.Length > MaxSlugLength
            || slug.EndsWith('-')
            || slug.Contains("--", StringComparison.Ordinal)
            || !HandleSlugPattern().IsMatch(slug))
        {
            error = $"Specialist handle '{rawHandle.Trim()}' is invalid. Use '@name' with 2-32 lowercase letters, digits, or single hyphens (starting with a letter, not ending with '-').";
            return false;
        }

        var candidate = "@" + slug;
        if (string.Equals(candidate, ReservedInoHandle, StringComparison.Ordinal))
        {
            error = "Handle '@ino' is reserved for Ino and cannot be assigned to a custom specialist.";
            return false;
        }

        normalized = candidate;
        return true;
    }
}

/// <summary>
/// Strict path confinement and cross-bot isolation rules for <see cref="MemoryScope.IsolatedBotScope"/>
/// rooted at <c>memory/bots/{appId}</c>.
/// </summary>
public static partial class BotMemoryScope
{
    public const string RootPrefix = "memory/bots/";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAppIdPattern();

    /// <summary>Normalizes and validates an app ID segment for <c>memory/bots/{appId}</c>.</summary>
    public static string NormalizeAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("Specialist app id is required for IsolatedBotScope.", nameof(appId));
        }

        var trimmed = appId.Trim();
        if (trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Contains('\0', StringComparison.Ordinal)
            || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"Specialist app id '{appId}' contains disallowed path characters.", nameof(appId));
        }

        var normalized = trimmed.ToLowerInvariant();
        if (!SafeAppIdPattern().IsMatch(normalized))
        {
            throw new ArgumentException($"Specialist app id '{appId}' must contain only lowercase letters, digits, '-' or '_'.", nameof(appId));
        }

        return normalized;
    }

    /// <summary>Returns the canonical isolated memory root key <c>memory/bots/{appId}</c>.</summary>
    public static string RootFor(string appId) => RootPrefix + NormalizeAppId(appId);

    /// <summary>Returns the isolated recall context key for a specialist bot so it never shares Ino's recall context.</summary>
    public static string RecallContextKeyFor(string appId) => $"bots/{NormalizeAppId(appId)}/context/memory.recall";

    /// <summary>Returns <see langword="true"/> when <paramref name="memoryKey"/> lies under <c>memory/bots/</c>.</summary>
    public static bool IsBotMemoryKey(string? memoryKey)
        => !string.IsNullOrWhiteSpace(memoryKey)
            && memoryKey.StartsWith(RootPrefix, StringComparison.Ordinal);

    /// <summary>Returns <see langword="true"/> when <paramref name="memoryKey"/> is a valid canonical <c>memory/bots/{appId}</c> root key.</summary>
    public static bool IsValidBotMemoryKey(string? memoryKey)
    {
        if (string.IsNullOrWhiteSpace(memoryKey) || !memoryKey.StartsWith(RootPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = memoryKey[RootPrefix.Length..];
        if (suffix.Contains('/', StringComparison.Ordinal)
            || suffix.Contains('\\', StringComparison.Ordinal)
            || suffix.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return SafeAppIdPattern().IsMatch(suffix);
    }

    private static readonly Regex SegmentPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Confines a relative memory path strictly inside <c>memory/bots/{appId}</c>.
    /// Rejects path traversal, cross-bot paths, absolute paths, and host secret/identity paths.
    /// </summary>
    public static string ResolveConfinedPath(string appId, string relativePath)
    {
        var root = RootFor(appId);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new UnauthorizedAccessException("Memory path must be non-empty.");
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal) || relativePath.Any(char.IsControl))
        {
            throw new UnauthorizedAccessException("Memory path contains control characters.");
        }

        var trimmed = relativePath.Trim();
        var unescaped = Uri.UnescapeDataString(trimmed);
        if (trimmed.Contains('\\', StringComparison.Ordinal)
            || unescaped.Contains('\\', StringComparison.Ordinal)
            || trimmed.StartsWith('/')
            || unescaped.StartsWith('/')
            || trimmed.StartsWith('~')
            || unescaped.StartsWith('~')
            || trimmed.Contains(':', StringComparison.Ordinal)
            || unescaped.Contains(':', StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes isolated bot memory root '{root}'.");
        }

        var rawSegments = trimmed.Split('/', StringSplitOptions.None);
        var unescapedSegments = unescaped.Split('/', StringSplitOptions.None);
        if (rawSegments.Length != unescapedSegments.Length)
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' escapes isolated bot memory root '{root}'.");
        }

        for (var i = 0; i < rawSegments.Length; i++)
        {
            if (!SegmentPattern.IsMatch(rawSegments[i]) || !SegmentPattern.IsMatch(unescapedSegments[i]))
            {
                throw new UnauthorizedAccessException($"Path '{relativePath}' contains invalid or traversal segments and cannot leave '{root}'.");
            }
        }

        var combined = root + "/" + string.Join('/', rawSegments);
        if (!combined.StartsWith(root + "/", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' is outside isolated bot memory root '{root}'.");
        }

        return combined;
    }

    /// <summary>
    /// Returns <see langword="true"/> only when <paramref name="targetMemoryKey"/> belongs strictly to
    /// <paramref name="callerAppId"/>'s isolated root <c>memory/bots/{callerAppId}</c>.
    /// </summary>
    public static bool CanAccess(string callerAppId, string? targetMemoryKey)
    {
        if (string.IsNullOrWhiteSpace(targetMemoryKey))
        {
            return false;
        }

        string root;
        try
        {
            root = RootFor(callerAppId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (string.Equals(targetMemoryKey, root, StringComparison.Ordinal))
        {
            return true;
        }

        if (!targetMemoryKey.StartsWith(root + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var tail = targetMemoryKey[(root.Length + 1)..];
        try
        {
            var confined = ResolveConfinedPath(callerAppId, tail);
            return string.Equals(confined, targetMemoryKey, StringComparison.Ordinal);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Fail-closed curated least-privilege contract allowlist for custom specialist bots.
/// Identity, secrets, custody, settings, host/kernel, confirmation, and device-shell contracts are always denied.
/// </summary>
public static class AppAgentContractPolicy
{
    public static readonly string[] CuratedAllowedContracts =
    [
        "IntoChat.OS.Modules.Web.IWebSearch",
        "IntoChat.OS.Modules.Web.IWebRead",
        typeof(IData).FullName!,
        typeof(IFocus).FullName!,
        typeof(Runs.IRuns).FullName!,
        typeof(Tasks.ITasks).FullName!,
        "IntoChat.OS.Modules.Microsoft.GitHub.IRepository",
        "IntoChat.OS.Modules.Microsoft.Roslyn.ICodeGraph",
    ];

    private static readonly HashSet<string> AllowedSet = new(CuratedAllowedContracts, StringComparer.Ordinal);

    /// <summary>Returns <see langword="true"/> only when <paramref name="contractFullName"/> is on the curated least-privilege allowlist.</summary>
    public static bool IsAllowedContract(string? contractFullName)
        => !string.IsNullOrWhiteSpace(contractFullName) && AllowedSet.Contains(contractFullName.Trim());

    /// <summary>
    /// Validates <paramref name="requestedContracts"/> fail-closed, returning a concrete normalized array.
    /// Throws <see cref="InvalidDataException"/> on blank, duplicate, or disallowed contracts; never silently widens or strips.
    /// </summary>
    public static string[] ValidateForManifest(string[]? requestedContracts)
    {
        if (requestedContracts is null || requestedContracts.Length == 0)
        {
            return [];
        }

        var result = new string[requestedContracts.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < requestedContracts.Length; i++)
        {
            var raw = requestedContracts[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new InvalidDataException("Every allowed contract entry must be a non-empty contract name.");
            }

            var contract = raw.Trim();
            if (!seen.Add(contract))
            {
                throw new InvalidDataException($"Allowed contract '{contract}' is listed more than once.");
            }

            if (!IsAllowedContract(contract))
            {
                throw new InvalidDataException(
                    $"Contract '{contract}' is not permitted for custom specialist bots (fail-closed least-privilege policy).");
            }

            result[i] = contract;
        }

        return result;
    }
}

/// <summary>
/// Pure-data agent declaration on an <see cref="AppManifest"/> (Rung 1 agent app / custom specialist bot).
/// </summary>
public sealed record AppAgent(
    string Instructions,
    ModelSelection? Model = null,
    string[]? Starters = null,
    string[]? Tools = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    string? Handle = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    string[]? AllowedContracts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    string[]? ToolSources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    MemoryScope MemoryScope = MemoryScope.None)
{
    public bool Equals(AppAgent? other) => other is not null
        && string.Equals(Instructions, other.Instructions, StringComparison.Ordinal)
        && EqualityComparer<ModelSelection?>.Default.Equals(Model, other.Model)
        && SequenceEqual(Starters, other.Starters)
        && SequenceEqual(Tools, other.Tools)
        && string.Equals(Handle, other.Handle, StringComparison.Ordinal)
        && SequenceEqual(AllowedContracts, other.AllowedContracts)
        && SequenceEqual(ToolSources, other.ToolSources)
        && MemoryScope == other.MemoryScope;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Instructions, StringComparer.Ordinal);
        hash.Add(Model);
        AddSequence(ref hash, Starters);
        AddSequence(ref hash, Tools);
        hash.Add(Handle, StringComparer.Ordinal);
        AddSequence(ref hash, AllowedContracts);
        AddSequence(ref hash, ToolSources);
        hash.Add(MemoryScope);
        return hash.ToHashCode();
    }

    private static bool SequenceEqual(string[]? left, string[]? right)
        => (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    private static void AddSequence(ref HashCode hash, string[]? items)
    {
        foreach (var item in items ?? [])
        {
            hash.Add(item, StringComparer.Ordinal);
        }
    }
}
