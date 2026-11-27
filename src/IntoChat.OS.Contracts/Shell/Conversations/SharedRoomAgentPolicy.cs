using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Flutter.Conversations;

/// <summary>
/// Marks a contract interface as safe to expose to agents running inside a <see cref="ThreadTrustPosture.CrossBrainShared"/> thread.
/// </summary>
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class RoomSafeAttribute : Attribute;

/// <summary>
/// Enforces strict private-brain confinement for agents and pinned screens in <see cref="ThreadTrustPosture.CrossBrainShared"/> threads:
/// empty <see cref="AgentDefinition.Contexts"/>, <see cref="AgentDefinition.IncludeComponentOperations"/> set to <see langword="false"/>,
/// <see cref="RoomSafeAttribute"/> contract allowlist only, explicit <c>@agentId</c> mention trigger only, and host-private screen denylist.
/// </summary>
public static partial class SharedRoomAgentPolicy
{
    public const string DefaultRoomAgentId = "room-ino";
    public const string DefaultRoomDisplayName = "@Ino · Room only";
    public const int MaxSquadHandoffDepth = 3;

    public static readonly SenderIdentity RoomAgentSender = new(DefaultRoomAgentId, DefaultRoomDisplayName, SenderKind.Agent, RoomScoped: true);

    /// <summary>Room-safe contracts only: strictly zero access to host personal brain, files, memory, secrets, or */* capabilities.</summary>
    public static readonly string[] RoomSafeContracts =
    [
        typeof(IConversation).FullName!,
    ];

    private static readonly string[] HostPrivatePrefixes =
    [
        "os.",
        "identity.",
        "social.",
        "component.",
        "synapse.",
        "flow.",
        "runtime.",
        "system.",
        "postgres.",
        "storage.",
        "device.",
        "mcp.",
        "ai.",
        "auth.",
    ];

    private static readonly HashSet<string> HostPrivateScreenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "files",
        "app.files",
        "ino",
        "app.ino",
        "settings",
        "app.settings",
        "tasks",
        "app.tasks",
        "engineer",
        "app.engineer",
        "creator",
        "app.creator",
        "desktop",
        "app.desktop",
        "friends",
        "app.friends",
        "devui",
        "app.devui",
        "orleans",
        "app.orleans",
        "aspire",
        "app.aspire",
        "widgetbook",
        "app.widgetbook",
        "approvals",
        "flows",
        "vault",
        "secrets",
    };

    private static readonly HashSet<string> HostPrivateCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System",
        "Developer",
        "Intelligence",
    };

    private const string RoomPersona = """
        You are @Ino · Room only, a shared AI assistant scoped strictly to this room ({room}).
        You can only see messages and pinned shareable mini-apps inside this room.
        You have zero access to any participant's private brain, personal files, private memory, settings, or secrets.
        Each human message carries an authenticated speaker name — always address participants accurately and never confuse speakers.
        Be concise, helpful, and collaborative for everyone in the room.
        """;

    [GeneratedRegex(@"(?:^|[\s(\[""',:;])@([a-zA-Z0-9][a-zA-Z0-9._-]{0,63})(?=$|[\s,.:;!?)\]\""'])", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();

    /// <summary>Returns <see langword="true"/> when <paramref name="screenType"/> is on the host-private denylist.</summary>
    public static bool IsHostPrivateScreenType(string screenType)
        => HostPrivateScreenTypes.Contains(screenType)
            || HostPrivatePrefixes.Any(prefix => screenType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns <see langword="true"/> when <paramref name="category"/> is a host-private screen category.</summary>
    public static bool IsHostPrivateCategory(string? category)
        => !string.IsNullOrWhiteSpace(category) && HostPrivateCategories.Contains(category);

    /// <summary>Extracts distinct <c>@handle</c> identifiers mentioned in <paramref name="text"/> in order of appearance.</summary>
    public static string[] ExtractMentions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in MentionPattern().Matches(text))
        {
            var handle = match.Groups[1].Value;
            if (seen.Add(handle))
            {
                result.Add(handle);
            }
        }
        return [.. result];
    }

    /// <summary>Returns <see langword="true"/> only when <paramref name="text"/> explicitly mentions <c>@agentId</c> (or <c>@Ino</c> for <see cref="DefaultRoomAgentId"/>).</summary>
    public static bool IsMentioned(string? text, string agentId = DefaultRoomAgentId)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        var mentions = ExtractMentions(text);
        foreach (var mention in mentions)
        {
            if (string.Equals(mention, agentId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(agentId, DefaultRoomAgentId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mention, "ino", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Returns <see langword="true"/> when <paramref name="contractFullName"/> is on the room-safe allowlist or marked <see cref="RoomSafeAttribute"/>.</summary>
    public static bool IsRoomSafeContract(string? contractFullName)
    {
        if (string.IsNullOrWhiteSpace(contractFullName))
        {
            return false;
        }

        if (RoomSafeContracts.Contains(contractFullName, StringComparer.Ordinal))
        {
            return true;
        }

        var type = typeof(IConversation).Assembly.GetType(contractFullName, throwOnError: false, ignoreCase: false);
        return type is { IsInterface: true } && type.IsDefined(typeof(RoomSafeAttribute), inherit: false);
    }

    /// <summary>Creates the default room-scoped <see cref="AgentDefinition"/> with strict private-brain isolation.</summary>
    public static AgentDefinition CreateRoomAgent(string? roomTitle = null, ModelSelection? model = null)
    {
        var label = string.IsNullOrWhiteSpace(roomTitle) ? "this room" : roomTitle.Trim();
        return new AgentDefinition
        {
            DisplayName = DefaultRoomDisplayName,
            Description = "Room-scoped assistant with access only to this room.",
            Instructions = RoomPersona.Replace("{room}", label, StringComparison.Ordinal).Trim(),
            Model = model,
            Contracts = [.. RoomSafeContracts],
            Contexts = [],
            IncludeComponentOperations = false,
            OperationApps = null,
            ToolSources = null,
        };
    }

    /// <summary>Confines any <see cref="AgentDefinition"/> to <see cref="ThreadTrustPosture.CrossBrainShared"/> policy invariants.</summary>
    public static AgentDefinition Confine(AgentDefinition definition, string? roomTitle = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var safeContracts = (definition.Contracts ?? [])
            .Where(IsRoomSafeContract)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (safeContracts.Length == 0)
        {
            safeContracts = [.. RoomSafeContracts];
        }

        var label = string.IsNullOrWhiteSpace(roomTitle) ? "this room" : roomTitle.Trim();
        var isolationPrefix = RoomPersona.Replace("{room}", label, StringComparison.Ordinal).Trim();
        var instructions = string.IsNullOrWhiteSpace(definition.Instructions)
            ? isolationPrefix
            : definition.Instructions.Contains("zero access to any participant's private brain", StringComparison.OrdinalIgnoreCase)
                ? definition.Instructions
                : isolationPrefix + "\n\n" + definition.Instructions;

        return definition with
        {
            Instructions = instructions,
            Contracts = safeContracts,
            Contexts = [],
            IncludeComponentOperations = false,
            OperationApps = null,
            ToolSources = null,
        };
    }

    /// <summary>Defense-in-depth runtime verification that a definition satisfies all <see cref="ThreadTrustPosture.CrossBrainShared"/> invariants.</summary>
    public static void AssertConfined(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Contexts.Length != 0
            || definition.IncludeComponentOperations
            || definition.OperationApps is not null
            || definition.ToolSources is not null
            || definition.Contracts.Any(c => !IsRoomSafeContract(c)))
        {
            throw new InvalidOperationException("SharedRoomAgentPolicy violated private-brain isolation invariants.");
        }
    }
}
