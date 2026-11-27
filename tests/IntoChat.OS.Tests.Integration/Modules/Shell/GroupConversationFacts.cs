using DigitalBrain;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Shell.Tests.Integration;

// Group chat moved exclusively onto IRoom (social.room). Personal conversations (IConversation) no longer expose
// Invite, IFriends no longer exposes GroupSay/GroupRead, and Returned tool outputs default to Visibility.Owner.
public sealed class GroupConversationFacts
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LegacyPersonalChatGroupMethodsAreRetired()
    {
        Assert.Null(typeof(IConversation).GetMethod("Invite"));
        Assert.Null(typeof(IFriends).GetMethod("GroupSay"));
        Assert.Null(typeof(IFriends).GetMethod("GroupRead"));
    }

    [Fact]
    public void ReturnedToolResultDefaultsToOwnerVisibility()
    {
        var returned = new Returned(
            new EntryId("r1/result/c1"),
            new Participant.Assistant("ino"),
            Now,
            "files_read",
            """{"secret":"private"}""",
            Ok: true,
            Card: null);

        Assert.Equal(Visibility.Owner, returned.Visible);
    }

    [Fact]
    public async Task PersonalConversationRefusesUninvitedFriendsAndHidesOwnerOnlyEntries()
    {
        await using var world = await World.Start();
        var called = new Called(new EntryId("r1/call/c1"), new Participant.Assistant("ino"), Now, "files_read", """{"path":"/work/secret"}""");
        var returned = new Returned(new EntryId("r1/result/c1"), new Participant.Assistant("ino"), Now, "files_read", """{"secret":"top-secret"}""", true, null);
        var summarized = new Summarized(new EntryId("r1/summary"), new Participant.Assistant("ino"), Now, "private summary", new EntryId("r1/call/c1"));
        var id = await world.NewConversation(Ada, "private-brain", called, returned, summarized);

        using (As(Ada))
        {
            await world.Conversation(Ada, id).Post("owner note", "note-1");
            var ownerView = await world.Conversation(Ada, id).Read();
            Assert.Contains(ownerView.Entries, e => e is Called);
            Assert.Contains(ownerView.Entries, e => e is Returned { Visible: Visibility.Owner });
            Assert.Contains(ownerView.Entries, e => e is Summarized);
        }

        // Even a confirmed friend cannot read or speak into Ada's personal conversation.
        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Conversation(Ada, id).Read());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Conversation(Ada, id).Say("sneaking in"));
        }
    }
}
