using DigitalBrain;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Tests.Integration.Files;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using Xunit;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Shell.Tests.Integration;

public sealed class GroupConversationFacts
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnInvitedFriendReadsAndSays()
    {
        await using var world = await World.Start();
        var id = await world.NewConversation(Ada, "trip");

        using (As(Ada)) { await world.Conversation(Ada, id).Invite(new AccountId(Ben)); }

        GroupInvite group;
        using (As(Ben))
        {
            group = Assert.Single(await world.Friends(Ben).Groups());
            Assert.Equal(("trip", new BrainId(Ada), id, new AccountId(Ada)), (group.Title, group.Brain, group.Conversation, group.By));
            await world.Friends(Ben).GroupSay(group, "hi from ben");
            var benView = await world.Friends(Ben).GroupRead(group);
            Assert.Contains(benView.Entries, e => e is Said { Text: "hi from ben", By: Participant.Human { Account.Value: Ben } });
        }

        using (As(Ada))
        {
            var adaView = await world.Conversation(Ada, id).Read();
            Assert.Contains(adaView.Entries, e => e is Said { Text: "hi from ben", By: Participant.Human { Account.Value: Ben } });
        }
    }

    [Fact]
    public async Task OnlyFriendsCanBeInvited()
    {
        await using var world = await World.Start();
        var id = await world.NewConversation(Ada, "trip");

        using (As(Ada))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Conversation(Ada, id).Invite(new AccountId(Eve)));
        }
    }

    [Fact]
    public async Task AnUnfriendedParticipantIsRefused()
    {
        await using var world = await World.Start();
        var group = await world.InviteBen("trip");

        using (As(Ada)) { await world.Friends(Ada).Unfriend(new AccountId(Ben)); }

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ben).GroupRead(group));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Ben).GroupSay(group, "still there?"));
        }
    }

    [Fact]
    public async Task FriendsSeeOnlyEveryoneEntries()
    {
        await using var world = await World.Start();
        var called = new Called(new EntryId("r1/call/c1"), new Participant.Assistant("ino"), Now, "files_read", """{"path":"/work/secret"}""");
        var summarized = new Summarized(new EntryId("r1/summary"), new Participant.Assistant("ino"), Now, "private summary", new EntryId("r1/call/c1"));
        var group = await world.InviteBen("trip", called, summarized);

        using (As(Ada)) { await world.Conversation(Ada, group.Conversation).Post("hello everyone", "welcome"); }

        using (As(Ada))
        {
            var ownerView = await world.Conversation(Ada, group.Conversation).Read();
            Assert.Contains(ownerView.Entries, e => e is Called);
            Assert.Contains(ownerView.Entries, e => e is Summarized);
        }

        using (As(Ben))
        {
            var friendView = await world.Friends(Ben).GroupRead(group);
            Assert.All(friendView.Entries, e => Assert.Equal(Visibility.Everyone, e.Visible));
            var only = Assert.Single(friendView.Entries);
            Assert.IsType<Said>(only);
            Assert.Equal("hello everyone", ((Said)only).Text);
            Assert.Null(friendView.Active);
            Assert.Empty(friendView.Runs);
        }
    }

    [Fact]
    public async Task AFriendsSayStartsNoTurn()
    {
        await using var world = await World.Start();
        var group = await world.InviteBen("trip");

        ConversationView before;
        using (As(Ada)) { before = await world.Conversation(Ada, group.Conversation).Read(); }

        using (As(Ben)) { await world.Friends(Ben).GroupSay(group, "just chatting"); }

        using (As(Ada))
        {
            var after = await world.Conversation(Ada, group.Conversation).Read();
            Assert.Null(after.Active);
            Assert.Equal(before.Runs.Length, after.Runs.Length);
            Assert.Contains(after.Entries, e => e is Said { Text: "just chatting", By: Participant.Human { Account.Value: Ben } });
        }
    }

    [Fact]
    public async Task ANonParticipantFriendIsRefused()
    {
        await using var world = await World.Start();
        var id = await world.NewConversation(Ada, "trip");
        var forged = new GroupInvite(new BrainId(Ada), id, "trip", new AccountId(Ada));

        // Seed the forged group entry before Eve's Friends neuron activates so Eve's relay genuinely calls Ada's conversation.
        await world.SeedForgedGroupInvite(Eve, forged);
        await world.Befriend(Ada, Eve);

        using (As(Eve))
        {
            // Refused at Ada's ConversationNeuron by GroupAdmission (Eve is a friend, not a participant).
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).GroupRead(forged));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).GroupSay(forged, "sneaking in"));
            // Also refused directly without the relay, and for an unlisted group invite at Eve's relay.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Conversation(Ada, id).Read());
            var unlisted = new GroupInvite(new BrainId(Ada), IntoChat.OS.Host.Primitives.ConversationId.New(), "other", new AccountId(Ada));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Friends(Eve).GroupRead(unlisted));
        }
    }
}
