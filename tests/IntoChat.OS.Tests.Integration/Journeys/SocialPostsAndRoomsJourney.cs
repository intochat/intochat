using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Calculator;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Contracts.Handles;
using IntoChat.OS.Tests.Support.Fakes;

namespace IntoChat.OS.Tests.Integration.Journeys;

public sealed class SocialPostsAndRoomsJourney
{
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AliceAndBobSharePostsReactionsAndARoomWithPinnedCalculatorAndPresence(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, _ => { }, ct);
        var alice = os.Person("alice");
        var bob = os.Person("bob");
        var charlie = os.Person("charlie");

        // 1. Alice and Bob become friends.
        await alice.Befriend(bob, ct);

        // 2. Alice publishes a post with audience [bob.Account] on IPosts.
        var alicePostsId = new NeuronId(alice.Brain, "social.posts", IPosts.DefaultKey);
        var alicePosts = alice.Get<IPosts>(alicePostsId);
        var postId = await alicePosts.Publish(new PostDraft("Hello Bob from IntoChat!", [bob.Account]), ct);
        Assert.False(string.IsNullOrWhiteSpace(postId));

        // 3. Bob pulls IPosts.Since(0), reacts "like", and comments; Alice sees both; Charlie is refused (offer.none).
        var bobViewOfAlicePosts = bob.Get<IPosts>(alicePostsId);
        var bobPage = await bobViewOfAlicePosts.Since(0, ct);
        var seenByBob = Assert.Single(bobPage.Posts);
        Assert.Equal(postId, seenByBob.Id);
        Assert.Equal("Hello Bob from IntoChat!", seenByBob.Text);
        Assert.True(bobPage.NextCursor >= seenByBob.Sequence);

        await bobViewOfAlicePosts.React(postId, "like", ct);
        var commentId = await bobViewOfAlicePosts.Comment(postId, "Nice post!", ct);
        Assert.False(string.IsNullOrWhiteSpace(commentId));

        var alicePage = await alicePosts.Since(0, ct);
        var updatedPost = Assert.Single(alicePage.Posts);
        var reaction = Assert.Single(updatedPost.Reactions);
        Assert.Equal(bob.Account, reaction.Account);
        Assert.Equal("like", reaction.Kind);
        var comment = Assert.Single(updatedPost.Comments);
        Assert.Equal(commentId, comment.Id);
        Assert.Equal(bob.Account, comment.Author);
        Assert.Equal("Nice post!", comment.Text);

        var charlieViewOfAlicePosts = charlie.Get<IPosts>(alicePostsId);
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => charlieViewOfAlicePosts.Since(0, ct));
        Assert.Contains("offer.none", refused.Message, StringComparison.Ordinal);

        // 4. Alice opens a room, opens & pins app.calculator, and invites Bob (Role.Editor).
        const string calcKey = "room-calc";
        await using var calcSession = await alice.Open<CalculatorApp>(calcKey, ct);
        var initialGen = calcSession.CurrentView.Generation;

        var roomId = new NeuronId(alice.Brain, "social.room", "hangout");
        var aliceRoom = alice.Get<IRoom>(roomId);
        await aliceRoom.Pin("app.calculator", calcKey, ct);
        await aliceRoom.Invite(bob.Account, bob.Brain, Role.Editor, ct);

        // 5. Bob receives the invite on IRooms, marks presence with Here(), says "hello", and acts on the pinned calculator.
        var bobRoomsId = new NeuronId(bob.Brain, "social.rooms", IRooms.DefaultKey);
        var bobRooms = bob.Get<IRooms>(bobRoomsId);
        var bobRoom = bob.Get<IRoom>(roomId);

        await bobRoom.Here(ct);
        await bobRoom.Say("hello", ct);

        var roomView = await bobRoom.Read(sinceRevision: null, ct);
        Assert.Contains(bob.Account, roomView.PresentAccounts);
        var msg = Assert.Single(roomView.Messages);
        Assert.Equal(bob.Account, msg.Author);
        Assert.Equal("hello", msg.Text);
        Assert.Contains(roomView.PinnedScreens, p => p.ScreenType == "app.calculator" && p.ScreenKey == calcKey);

        // Press key-0 ("7") on the pinned CalculatorApp with current generation.
        var afterPress = await bobRoom.Act("app.calculator", calcKey, new(initialGen, "key-0"), ct);
        Assert.True(afterPress.Generation > initialGen);

        // A stale generation is refused with ActivationRefusedException.Stale.
        var stale = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => bobRoom.Act("app.calculator", calcKey, new(initialGen, "key-0"), ct));
        Assert.Equal(ActivationRefusedException.Stale, stale.Reason);

        // 6. Bob's IRooms.LastRevision(roomId) reflects the Changed nudges.
        var lastRev = await bobRooms.LastRevision(roomId, ct);
        Assert.True(lastRev >= 2, $"Expected Bob's IRooms.LastRevision >= 2 after Say + Act, got {lastRev}.");
    }

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task ProfileHandleUpdateClaimsInHandleDirectory(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, _ => { }, ct);
        var alice = os.Person("alice");
        var handle = $"alice-{kind.ToString().ToLowerInvariant()}";

        var profile = alice.Get<IProfile>(new NeuronId(alice.Brain, "os.profile", "profile"));
        var updated = await profile.UpdateProfile(handle, "Alice", "Building social", []);
        Assert.Equal(handle, updated.Handle);

        var normalized = IHandleDirectory.Normalize(handle);
        var handleId = new NeuronId(new BrainId(IHandleDirectory.BrainOf(normalized)), IHandleDirectory.TypeName, $"handles/{normalized}");
        var directory = alice.Get<IHandleDirectory>(handleId);
        Assert.Equal(alice.Brain.Value, await directory.Resolve());
    }
}
