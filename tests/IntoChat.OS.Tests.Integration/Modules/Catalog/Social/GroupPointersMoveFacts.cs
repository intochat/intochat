using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Tests;
using Microsoft.Extensions.DependencyInjection;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Social;

// A person's invitations to friends' conversations are kept by their rooms index (social.rooms), beside the rooms they
// were invited to. The roster kept them before: a roster written by an earlier version hands them over on its next use
// and stores an empty list. These facts run on a silo and read what is stored from the brain's journal.
public sealed class GroupPointersMoveFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ConversationId Trip = ConversationId.Parse("c-0123456789abcdef0123456789abcdef");
    private static readonly ConversationId Work = ConversationId.Parse("c-fedcba9876543210fedcba9876543210");

    private static GroupInvite[] Earlier() =>
    [
        new(new BrainId(Ada), Trip, "Trip", new AccountId(Ada)),
        new(new BrainId(Ada), Work, "Work", new AccountId(Ada)),
    ];

    private static IThreadDirectory Rooms(World world, string brain)
        => SiloNeurons.Get<IThreadDirectory>(world.Runtime.Services, new NeuronId(new BrainId(brain), Threads.Type, IThreadDirectory.DefaultKey));

    private static string Line(GroupInvite group) => $"{group.Brain.Value}|{group.Conversation.Value}|{group.Title}|{group.By.Value}";

    private static string Line(ConversationInvite invite) => $"{invite.Brain.Value}|{invite.Conversation.Value}|{invite.Title}|{invite.By.Value}";

    private static readonly string[] Both = [$"ada|{Trip.Value}|Trip|ada", $"ada|{Work.Value}|Work|ada"];

    [Fact]
    public async Task ARosterOfAnEarlierVersionHandsItsInvitationsToTheRoomsIndexAndKeepsNone()
    {
        await using var world = await World.Start();
        await world.Befriend(Ada, Ben);
        await Rewrite(world, Ben, roster => roster.Groups = Earlier());
        Assert.Contains("\"title\":\"Trip\"", await Stored(world, Ben, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);
        Assert.Equal("", await Stored(world, Ben, Threads.Type, IThreadDirectory.DefaultKey));

        // Any use of the roster by its owner: here, listing friends.
        using (As(Ben)) { Assert.Single(await world.Friends(Ben).List()); }

        // The roster keeps its other fields and no invitation.
        var roster = await Stored(world, Ben, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
        Assert.Contains("\"groups\":[]", roster, StringComparison.Ordinal);
        Assert.Contains("\"account\":\"ada\"", roster, StringComparison.Ordinal);
        Assert.DoesNotContain("Trip", roster, StringComparison.Ordinal);

        // The rooms index holds them in the order the roster kept them, the conversation by its id text.
        var index = await Stored(world, Ben, Threads.Type, IThreadDirectory.DefaultKey);
        Assert.Contains(
            $$"""
            "conversations":[{"brain":"ada","conversation":"{{Trip.Value}}","title":"Trip","by":"ada"},{"brain":"ada","conversation":"{{Work.Value}}","title":"Work","by":"ada"}]
            """.Trim(),
            index,
            StringComparison.Ordinal);

        using (As(Ben))
        {
            Assert.Equal(Both, (await Rooms(world, Ben).Conversations()).Select(Line));
            // The roster still answers, from the rooms index.
            Assert.Equal(Both, (await world.Friends(Ben).Groups()).Select(Line));
        }
    }

    // Interrupted after the rooms index took the invitations and before the roster was stored without them: both hold the
    // same list. A reader gets each invitation once, the rooms index is not written again, and the roster is finished.
    [Fact]
    public async Task AMoveInterruptedBetweenCopyAndClearIsReadOnceAndFinishedByTheNextCall()
    {
        await using var world = await World.Start();
        await world.Befriend(Ada, Ben);
        await Rewrite(world, Ben, roster => roster.Groups = Earlier());
        using (As(Ben)) { await world.Friends(Ben).List(); }
        var kept = await Stored(world, Ben, Threads.Type, IThreadDirectory.DefaultKey);

        // The state a stop between the two steps leaves: the roster record holds the list again while it sleeps.
        await Rewrite(world, Ben, roster => roster.Groups = Earlier());
        Assert.Contains("Trip", await Stored(world, Ben, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);

        using (As(Ben)) { Assert.Equal(Both, (await world.Friends(Ben).Groups()).Select(Line)); }

        Assert.Contains("\"groups\":[]", await Stored(world, Ben, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);
        Assert.Equal(kept, await Stored(world, Ben, Threads.Type, IThreadDirectory.DefaultKey));
        using (As(Ben)) { Assert.Equal(Both, (await Rooms(world, Ben).Conversations()).Select(Line)); }
    }

    // An invitation forwarded through the roster is kept by the rooms index and not by the roster.
    [Fact]
    public async Task ANewInvitationIsKeptByTheRoomsIndexAndNotByTheRoster()
    {
        await using var world = await World.Start();
        await world.Befriend(Ada, Ben);
        await Rewrite(world, Ben, roster => roster.Groups = Earlier());

        var dinnerId = await world.NewConversation(Ada, "Dinner");
        var fromAda = new NeuronId(new BrainId(Ada), IConversation.Type, IConversation.KeyOf(dinnerId));
        var invited = new GroupInvite(new BrainId(Ada), dinnerId, "Dinner", new AccountId(Ada));
        using (Relayed(Ada, fromAda)) { await world.Friends(Ben).Invited(invited); }

        var roster = await Stored(world, Ben, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
        Assert.Contains("\"groups\":[]", roster, StringComparison.Ordinal);
        Assert.DoesNotContain("Dinner", roster, StringComparison.Ordinal);
        using (As(Ben))
        {
            Assert.Equal([.. Both, $"ada|{invited.Conversation.Value}|Dinner|ada"], (await Rooms(world, Ben).Conversations()).Select(Line));
        }
    }

    // Who may write into the index of invitations: this brain's own roster, and the conversation of a friend who is the
    // inviter. Everything the invitation itself says is compared with the caller the kernel stamped.
    [Fact]
    public async Task OnlyTheRosterAndAFriendsOwnConversationWriteInvitations()
    {
        await using var world = await World.Start();
        await world.Befriend(Ada, Ben);
        var invite = new ConversationInvite(new BrainId(Ada), Trip, "Trip", new AccountId(Ada));
        var fromAda = new NeuronId(new BrainId(Ada), IConversation.Type, IConversation.KeyOf(Trip));
        var index = Rooms(world, Ben);

        // The owner as a person, an app and an agent of the brain: not the roster.
        foreach (var who in (Who[])[Who.Person, Who.App, Who.Agent])
        {
            using (As(Ben, kind: who))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.AdoptConversations([invite]));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite));
            }
        }

        // A friend in person, and a stranger: not a conversation.
        foreach (var person in (string[])[Ada, Eve])
        {
            using (As(person))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.AdoptConversations([invite]));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(index.Conversations);
            }
        }

        // A stranger's conversation; a friend's conversation naming another conversation, another brain or another inviter.
        var fromEve = new NeuronId(new BrainId(Eve), IConversation.Type, IConversation.KeyOf(Trip));
        using (Relayed(Eve, fromEve))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite with { Brain = new BrainId(Eve), By = new AccountId(Eve) }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite));
        }
        using (Relayed(Ada, fromAda))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite with { Conversation = Work }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite with { Brain = new BrainId(Eve) }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => index.InvitedToConversation(invite with { By = new AccountId(Eve) }));
        }
        using (As(Ben)) { Assert.Empty(await index.Conversations()); }
        Assert.DoesNotContain("Trip", await Stored(world, Ben, Threads.Type, IThreadDirectory.DefaultKey), StringComparison.Ordinal);

        // The friend's own conversation, naming itself: taken. This is the call a later release sends in place of the
        // roster's; no conversation sends it yet.
        using (Relayed(Ada, fromAda)) { await index.InvitedToConversation(invite); }
        using (As(Ben)) { Assert.Equal([Both[0]], (await index.Conversations()).Select(Line)); }
    }

    // A person's call carried by a neuron of their own brain, as the kernel stamps a call one brain's neuron makes into another.
    private static IDisposable Relayed(string account, NeuronId via)
        => World.Edge.Enter(World.Edge.RelayedPerson(account, account, new BrainId(account), via, inside: true));

    private static string GrainId(string brain, string type, string key) => $"neuron.host/{brain}/{type}/{key}";

    // What the brain's journal holds for the neuron, as text; empty when it holds nothing.
    private static async Task<string> Stored(World world, string brain, string type, string key)
    {
        var journal = world.Runtime.Services.GetRequiredService<IBrainJournalWriter>();
        await journal.Claim(brain, Ct);
        return await journal.ReadState(brain, GrainId(brain, type, key), "state", Ct) is { } stored ? Encoding.UTF8.GetString(stored.Record.Span) : "";
    }

    // Replaces the roster's stored record while the roster sleeps.
    private static async Task Rewrite(World world, string brain, Action<FriendsRecord> change)
    {
        await world.Runtime.Deactivate(FriendsDefaults.Id(brain));
        var journal = world.Runtime.Services.GetRequiredService<IBrainJournalWriter>();
        var grainId = GrainId(brain, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
        var stored = await journal.ReadState(brain, grainId, "state", Ct) ?? throw new InvalidOperationException($"{brain} has no roster yet.");
        var roster = JsonSerializer.Deserialize(stored.Record.Span, FriendsJson.Default.FriendsRecord)!;
        change(roster);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(roster, FriendsJson.Default.FriendsRecord);
        await journal.Append(brain, new JournalStateWrite(grainId, "state", JournalStateChange.Write, stored.ETag, bytes, stored.StateType), "", [], Ct);
    }
}
