using System.Reflection;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Screens;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Flutter.Conversations;

/// <summary>
/// Marks a contract interface, screen signal record, or <c>[On]</c> handler method as safe to expose to agents
/// running inside a <see cref="ThreadTrustPosture.CrossBrainShared"/> thread.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Class, Inherited = false)]
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
    public const string RoomListPinnedAppsToolName = "room_list_pinned_apps";
    public const string RoomActToolName = "room_act";
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

    /// <summary>
    /// Fail-closed validation shared by <c>ConversationNeuron</c> and <c>AppUiEndpoints</c> to ensure a screen class
    /// is a registered, non-internal <see cref="IScreen"/> marked <see cref="ShareableAttribute"/> and outside host-private
    /// types and categories.
    /// </summary>
    public static void ValidateShareableScreenClass(string screenType, Type? screenClass, string? contextLabel = null)
    {
        var normalizedType = (screenType ?? string.Empty).Trim();
        var appPrefixedType = normalizedType.Contains('.', StringComparison.Ordinal) ? normalizedType : "app." + normalizedType;
        var suffix = string.IsNullOrWhiteSpace(contextLabel) ? string.Empty : $" in {contextLabel}";

        if (string.IsNullOrWhiteSpace(normalizedType)
            || IsHostPrivateScreenType(normalizedType)
            || IsHostPrivateScreenType(appPrefixedType))
        {
            throw new UnauthorizedAccessException($"Screen type '{normalizedType}' is host-private and denied{suffix}.");
        }

        if (screenClass is null || !typeof(IScreen).IsAssignableFrom(screenClass))
        {
            throw new UnauthorizedAccessException($"Screen type '{normalizedType}' is not a registered shareable screen{suffix}.");
        }

        if (!screenClass.IsDefined(typeof(ShareableAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{normalizedType}' is not marked [Shareable]{suffix}.");
        }

        if (screenClass.IsDefined(typeof(InternalAttribute), inherit: false))
        {
            throw new UnauthorizedAccessException($"Screen type '{normalizedType}' is internal and denied{suffix}.");
        }

        foreach (var attr in screenClass.GetCustomAttributesData())
        {
            if (string.Equals(attr.AttributeType.Name, "ScreenAttribute", StringComparison.Ordinal))
            {
                foreach (var named in attr.NamedArguments)
                {
                    if (string.Equals(named.MemberName, "Category", StringComparison.Ordinal)
                        && named.TypedValue.Value is string category
                        && IsHostPrivateCategory(category))
                    {
                        throw new UnauthorizedAccessException($"Screen type '{normalizedType}' (category '{category}') is host-private and denied{suffix}.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Fail-closed validation for <c>room_act</c>: verifies that <paramref name="signalAlias"/> resolves to a declared
    /// <see cref="OnAttribute"/> handler on <paramref name="screenClass"/> that is explicitly marked
    /// <see cref="RoomSafeAttribute"/> (on the handler or its signal type) and NOT marked <see cref="PersonOnlyAttribute"/>.
    /// </summary>
    public static void ValidateRoomSafeScreenSignal(Type screenClass, string signalAlias)
    {
        ArgumentNullException.ThrowIfNull(screenClass);
        if (string.IsNullOrWhiteSpace(signalAlias))
        {
            throw new UnauthorizedAccessException("room_act requires a non-empty signal name.");
        }

        var normalized = signalAlias.Trim();
        foreach (var method in screenClass.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (!method.IsDefined(typeof(OnAttribute), inherit: true))
            {
                continue;
            }

            var parameters = method.GetParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            var signalType = parameters[0].ParameterType;
            var signalAttr = signalType.GetCustomAttribute<SignalAttribute>(inherit: false);
            var alias = signalAttr?.Alias ?? signalType.Name;
            if (!string.Equals(alias, normalized, StringComparison.Ordinal))
            {
                continue;
            }

            if (HasPersonOnlyAttribute(method) || HasPersonOnlyAttribute(signalType))
            {
                throw new UnauthorizedAccessException($"Signal '{normalized}' on '{screenClass.Name}' is [PersonOnly] and cannot be invoked by room @Ino.");
            }

            var isRoomSafe = method.IsDefined(typeof(RoomSafeAttribute), inherit: false)
                || signalType.IsDefined(typeof(RoomSafeAttribute), inherit: false);
            if (!isRoomSafe)
            {
                throw new UnauthorizedAccessException($"Signal '{normalized}' on '{screenClass.Name}' is not marked [RoomSafe] and cannot be invoked by room @Ino.");
            }

            return;
        }

        throw new UnauthorizedAccessException($"Signal '{normalized}' is not a declared [RoomSafe] handler on '{screenClass.Name}'.");
    }

    private static bool HasPersonOnlyAttribute(MemberInfo member)
        => member.GetCustomAttributes(inherit: true)
            .Any(static attr => string.Equals(attr.GetType().Name, "PersonOnlyAttribute", StringComparison.Ordinal));

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
            MemoryScopeKey = null,
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
            || definition.MemoryScopeKey is not null
            || definition.Contracts.Any(c => !IsRoomSafeContract(c)))
        {
            throw new InvalidOperationException("SharedRoomAgentPolicy violated private-brain isolation invariants.");
        }
    }
}
