using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;

namespace IntoChat.OS.Apps.AgentApp;

/// <summary>
/// Pure policy for pure-data custom specialist bots: dedicated <c>agent-{appId}</c> conversation/agent identity,
/// normalized <c>@handle</c> validation and uniqueness, fail-closed curated <see cref="AppAgent.AllowedContracts"/>,
/// <see cref="MemoryScope.IsolatedBotScope"/> confinement, isolated <see cref="AgentDefinition"/> projection, and seating equivalence.
/// </summary>
public static class AgentAppPolicy
{
    public const string ConversationPrefix = "agent-";

    /// <summary>Curated least-privilege contracts permitted for custom specialist bots (fail-closed).</summary>
    public static string[] CuratedAllowedContracts => AppAgentContractPolicy.CuratedAllowedContracts;

    /// <summary>Returns the dedicated conversation ID for an agent app, disjoint from Ino's session IDs.</summary>
    public static ConversationId ConversationOf(string appId)
        => ConversationId.Parse(ConversationPrefix + NormalizeAppId(appId));

    /// <summary>Returns the seated agent ID for an agent app.</summary>
    public static string AgentIdOf(string appId)
        => ConversationPrefix + NormalizeAppId(appId);

    /// <summary>Returns <see langword="true"/> when <paramref name="conversationId"/> is a specialist <c>agent-{appId}</c> thread.</summary>
    public static bool IsSpecialistConversationId(string? conversationId)
        => TryAppIdOf(conversationId, out _);

    /// <summary>Extracts the normalized specialist app ID from an <c>agent-{appId}</c> conversation ID.</summary>
    public static bool TryAppIdOf(string? conversationId, out string appId)
    {
        appId = string.Empty;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        var trimmed = conversationId.Trim();
        if (!trimmed.StartsWith(ConversationPrefix, StringComparison.Ordinal) || trimmed.Length <= ConversationPrefix.Length)
        {
            return false;
        }

        var candidate = trimmed[ConversationPrefix.Length..];
        if (candidate.Contains('/', StringComparison.Ordinal) || candidate.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            appId = NormalizeAppId(candidate);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Normalizes and validates a specialist <c>@handle</c> (enforcing syntax and rejecting reserved <c>@ino</c>).</summary>
    public static string NormalizeHandle(string? rawHandle)
        => AppAgentHandle.Normalize(rawHandle);

    /// <summary>Attempts to normalize and validate a specialist <c>@handle</c>.</summary>
    public static bool TryNormalizeHandle(string? rawHandle, out string normalized, out string? error)
        => AppAgentHandle.TryNormalize(rawHandle, out normalized, out error);

    /// <summary>Ensures <paramref name="normalizedHandle"/> is not already claimed by another specialist app.</summary>
    public static void EnsureUniqueHandle(
        string normalizedHandle,
        string appId,
        IEnumerable<(string AppId, string? Handle)> existingSpecialists,
        bool allowSelfUpdate = true)
    {
        var canonicalHandle = AppAgentHandle.Normalize(normalizedHandle);
        var targetApp = NormalizeAppId(appId);
        ArgumentNullException.ThrowIfNull(existingSpecialists);

        foreach (var (existingAppId, existingHandle) in existingSpecialists)
        {
            if (string.IsNullOrWhiteSpace(existingAppId) || string.IsNullOrWhiteSpace(existingHandle))
            {
                continue;
            }

            if (allowSelfUpdate && string.Equals(NormalizeAppId(existingAppId), targetApp, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AppAgentHandle.TryNormalize(existingHandle, out var otherHandle, out _)
                && string.Equals(otherHandle, canonicalHandle, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Handle '{canonicalHandle}' is already used by specialist '{existingAppId}'. Choose a unique handle.");
            }
        }
    }

    /// <summary>Returns <see langword="true"/> only when <paramref name="contractFullName"/> is on the curated least-privilege allowlist.</summary>
    public static bool IsAllowedContract(string? contractFullName)
        => AppAgentContractPolicy.IsAllowedContract(contractFullName);

    /// <summary>
    /// Validates requested specialist contracts fail-closed.
    /// Rejects any identity, secrets, custody, settings, host, or non-curated contract with <see cref="UnauthorizedAccessException"/>;
    /// never silently widens or strips permissions.
    /// </summary>
    public static string[] ValidateAllowedContracts(string[]? requestedContracts)
    {
        if (requestedContracts is null || requestedContracts.Length == 0)
        {
            return [];
        }

        var validated = new string[requestedContracts.Length];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < requestedContracts.Length; i++)
        {
            var raw = requestedContracts[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new UnauthorizedAccessException("Specialist AllowedContracts cannot contain blank entries.");
            }

            var contract = raw.Trim();
            if (!seen.Add(contract))
            {
                throw new InvalidDataException($"Specialist AllowedContracts lists '{contract}' more than once.");
            }

            if (!AppAgentContractPolicy.IsAllowedContract(contract))
            {
                throw new UnauthorizedAccessException(
                    $"Contract '{contract}' is denied by AgentAppPolicy (fail-closed least-privilege allowlist).");
            }

            validated[i] = contract;
        }

        return validated;
    }

    /// <summary>Projects an agent app's manifest and bound connection slots into an isolated <see cref="AgentDefinition"/>.</summary>
    public static AgentDefinition Definition(AppManifest app, IReadOnlyDictionary<string, string>? boundSlots, string? appId = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var agent = app.Agent ?? throw new InvalidOperationException("App manifest does not declare an agent.");

        if (!Enum.IsDefined(agent.MemoryScope))
        {
            throw new InvalidOperationException($"Unsupported specialist memory scope '{agent.MemoryScope}'.");
        }

        if (agent.Handle is not null)
        {
            _ = AppAgentHandle.Normalize(agent.Handle);
        }

        var allowedContracts = ValidateAllowedContracts(agent.AllowedContracts);

        var declaredSlots = new HashSet<string>(
            (app.Slots ?? []).Where(static s => s is not null && !string.IsNullOrWhiteSpace(s.Name)).Select(static s => s.Name),
            StringComparer.Ordinal);

        var sources = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (agent.Tools is { Length: > 0 } toolSlots && boundSlots is { Count: > 0 })
        {
            foreach (var slotName in toolSlots)
            {
                if (string.IsNullOrWhiteSpace(slotName) || !declaredSlots.Contains(slotName))
                {
                    continue;
                }

                if (boundSlots.TryGetValue(slotName, out var canonicalKey)
                    && !string.IsNullOrWhiteSpace(canonicalKey)
                    && seen.Add(canonicalKey.Trim()))
                {
                    sources.Add(canonicalKey.Trim());
                }
            }
        }

        if (agent.ToolSources is { Length: > 0 } directSources)
        {
            foreach (var source in directSources)
            {
                if (!string.IsNullOrWhiteSpace(source) && seen.Add(source.Trim()))
                {
                    sources.Add(source.Trim());
                }
            }
        }

        string? memoryScopeKey = null;
        string[] contexts = [];
        if (agent.MemoryScope == MemoryScope.IsolatedBotScope)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                throw new InvalidOperationException("IsolatedBotScope requires a specialist appId to root memory at memory/bots/{appId}.");
            }

            memoryScopeKey = BotMemoryScope.RootFor(appId);
            contexts = [BotMemoryScope.RecallContextKeyFor(appId)];
        }

        return new AgentDefinition
        {
            DisplayName = string.IsNullOrWhiteSpace(app.Title) ? "Agent" : app.Title.Trim(),
            Description = app.Description ?? string.Empty,
            Instructions = agent.Instructions,
            Model = agent.Model,
            Contracts = allowedContracts,
            Contexts = contexts,
            IncludeComponentOperations = false,
            ToolSources = [.. sources],
            MemoryScopeKey = memoryScopeKey,
        };
    }

    /// <summary>Returns true when two <see cref="AgentDefinition"/> values produce the same conversation seat.</summary>
    public static bool SameSeat(AgentDefinition? seated, AgentDefinition wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        if (seated is null)
        {
            return false;
        }

        return string.Equals(seated.DisplayName, wanted.DisplayName, StringComparison.Ordinal)
            && string.Equals(seated.Description, wanted.Description, StringComparison.Ordinal)
            && string.Equals(seated.Instructions, wanted.Instructions, StringComparison.Ordinal)
            && Equals(seated.Model, wanted.Model)
            && seated.IncludeComponentOperations == wanted.IncludeComponentOperations
            && (seated.Contracts ?? []).SequenceEqual(wanted.Contracts ?? [], StringComparer.Ordinal)
            && (seated.Contexts ?? []).SequenceEqual(wanted.Contexts ?? [], StringComparer.Ordinal)
            && SameToolSources(seated.ToolSources, wanted.ToolSources)
            && string.Equals(seated.MemoryScopeKey, wanted.MemoryScopeKey, StringComparison.Ordinal);
    }

    private static bool SameToolSources(string[]? left, string[]? right)
        => (left is null && right is null)
            || (left is not null && right is not null && left.SequenceEqual(right, StringComparer.Ordinal));

    private static string NormalizeAppId(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var normalized = appId.Trim().ToLowerInvariant();
        var chars = new char[normalized.Length];
        for (var i = 0; i < normalized.Length; i++)
        {
            var ch = normalized[i];
            chars[i] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-';
        }
        var cleaned = new string(chars).Trim('-');
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("App id must contain at least one alphanumeric character.", nameof(appId));
        }
        return cleaned;
    }
}
