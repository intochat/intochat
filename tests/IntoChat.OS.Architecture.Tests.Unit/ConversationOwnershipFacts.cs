using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// One conversation aggregate owns the transcript; no chat projection or adapter sits beside it,
// and Ino never addresses IAgent directly to carry a conversation.
public sealed partial class ConversationOwnershipFacts
{
    [GeneratedRegex("^(IChat|I?Chat(Neuron|Binding|AgentSource)|AgentChatSource)$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyChatType();

    [Fact]
    public void NothingButTheConversationWritesAConversation()
    {
        var offenders = ProductionArchitecture.Types
            .Where(type => LegacyChatType().IsMatch(type.Name))
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void InoNeverCallsTheAgentToTalk()
    {
        var inoApp = Path.Combine(Repository.ProjectNamed("IntoChat.OS.Apps").Directory, "Ino", "InoApp.cs");
        Assert.DoesNotContain("IAgent>(", File.ReadAllText(inoApp), StringComparison.Ordinal);
    }
}
