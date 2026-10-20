using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;

namespace IntoChat.OS.Kernel.Tests.Unit;

// Only posted runs drop the user bubble; an attachment-only user turn (empty text) keeps it.
public sealed class AgentChatSourceFacts
{
    [Fact]
    public void AnAttachmentOnlyUserTurnStillShowsItsUserBubble()
    {
        var run = new AgentRun("r1", AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: "A cat.");
        Assert.Equal([ChatRole.User, ChatRole.Assistant], AgentChatSource.Entries(run).Select(entry => entry.Role));
    }

    [Fact]
    public void APostedRunShowsOneAssistantBubble()
    {
        var run = new AgentRun("post/greeting", AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: "Hi") { Posted = true };
        Assert.Equal([ChatRole.Assistant], AgentChatSource.Entries(run).Select(entry => entry.Role));
    }
}
