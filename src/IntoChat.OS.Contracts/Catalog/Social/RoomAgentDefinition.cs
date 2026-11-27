using System.Text.RegularExpressions;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps.Social;

/// <summary>
/// Strictly room-scoped @Ino agent definition for multi-participant friend and group rooms.
/// Enforces private-brain isolation: empty <see cref="AgentDefinition.Contexts"/>,
/// <see cref="AgentDefinition.IncludeComponentOperations"/> set to <see langword="false"/>,
/// and room-safe contracts only.
/// </summary>
public static partial class RoomAgentDefinition
{
    public const string AgentId = "room-ino";
    public const string DisplayName = "@Ino · Room only";

    public static readonly SenderIdentity AgentSender = new(AgentId, DisplayName, SenderKind.Agent);

    /// <summary>Room-safe contracts only: strictly zero access to host personal brain, files, memory, secrets, or */* capabilities.</summary>
    public static readonly string[] RoomSafeContracts =
    [
        typeof(IRoom).FullName!,
    ];

    private const string RoomPersona = """
        You are @Ino · Room only, a shared AI assistant scoped strictly to this room ({room}).
        You can only see messages and pinned shareable mini-apps inside this room.
        You have zero access to any participant's private brain, personal files, private memory, settings, or secrets.
        Each human message carries an authenticated speaker name — always address participants accurately and never confuse speakers.
        Be concise, helpful, and collaborative for everyone in the room.
        """;

    [GeneratedRegex(@"(?:^|[\s(\[""',:;])@ino(?=$|[\s,.:;!?)\]\""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InoMentionPattern();

    /// <summary>Returns <see langword="true"/> only when <paramref name="text"/> explicitly mentions <c>@Ino</c>.</summary>
    public static bool IsMentioned(string? text)
        => !string.IsNullOrWhiteSpace(text) && InoMentionPattern().IsMatch(text);

    /// <summary>Creates a room-scoped <see cref="AgentDefinition"/> with strict private-brain isolation.</summary>
    public static AgentDefinition Create(string? roomTitle = null, ModelSelection? model = null)
    {
        var label = string.IsNullOrWhiteSpace(roomTitle) ? "this room" : roomTitle.Trim();
        return new AgentDefinition
        {
            DisplayName = DisplayName,
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
}
