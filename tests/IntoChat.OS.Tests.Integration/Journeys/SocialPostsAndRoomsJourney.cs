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

        // Press key-0 ("7") on the pinned CalculatorApp: the room sends that key's command through the screen's Dispatch.
        var afterPress = await bobRoom.Act("app.calculator", calcKey, calcSession.CurrentView.CommandAt("key-0"), ct);
        Assert.True(afterPress.Generation > initialGen);

        // A command to a child the calculator does not show is refused before any handler runs.
        var unknown = await Assert.ThrowsAsync<ActivationRefusedException>(
            () => bobRoom.Act("app.calculator", calcKey, calcSession.CurrentView.CommandAt("key-0") with { Target = "no-such-child" }, ct));
        Assert.Equal(ActivationRefusedException.UnknownChild, unknown.Reason);

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

    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task BobForksAlicesSharedAppDefinitionWithEmptyStateAndNoBindingsOfHers(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = await Journey.Start(kind, _ => { }, ct);
        var alice = os.Person("alice");
        var bob = os.Person("bob");

        // 1. Alice and Bob become friends.
        await alice.Befriend(bob, ct);

        // 2. Alice binds her own instance of "customer-notes" in her brain, then shares ONLY its app definition.
        var aliceNotesTarget = new NeuronId(alice.Brain, "postgres.table", "alice-private-notes");
        var aliceInstalls = alice.Get<IInstalls>(new NeuronId(alice.Brain, "synapse.installs", "default"));
        await aliceInstalls.BindComponent(
            new ComponentId("customer-notes"),
            [new Binding("notes", new BindingTarget.Local(aliceNotesTarget))],
            ct);

        var alicePostsId = new NeuronId(alice.Brain, "social.posts", IPosts.DefaultKey);
        var alicePosts = alice.Get<IPosts>(alicePostsId);
        var share = new AppDefinitionShare(
            "customer-notes",
            "Customer Notes",
            [new SharedSourceFile("App.cs", "// unit source")]);
        var postId = await alicePosts.Publish(
            new PostDraft("Sharing my Customer Notes app!", [bob.Account], SharedApp: share),
            ct);
        Assert.False(string.IsNullOrWhiteSpace(postId));

        // 3. Bob pulls Alice's post and receives only source files + title/id (no author state, no author bindings).
        var bobViewOfAlicePosts = bob.Get<IPosts>(alicePostsId);
        var bobPage = await bobViewOfAlicePosts.Since(0, ct);
        var seenByBob = Assert.Single(bobPage.Posts);
        Assert.Equal(postId, seenByBob.Id);
        var received = Assert.IsType<AppDefinitionShare>(seenByBob.SharedApp);
        Assert.Equal("customer-notes", received.AppId);
        Assert.Equal("Customer Notes", received.Title);
        var file = Assert.Single(received.Files);
        Assert.Equal("App.cs", file.Path);
        Assert.Equal("// unit source", file.Content);

        // 4. Bob forks/binds the shared app definition in Bob's own brain with Bob's own Local binding.
        var bobNotesTarget = new NeuronId(bob.Brain, "postgres.table", "bob-notes");
        var bobInstalls = bob.Get<IInstalls>(new NeuronId(bob.Brain, "synapse.installs", "default"));
        await bobInstalls.BindComponent(
            new ComponentId(received.AppId),
            [new Binding("notes", new BindingTarget.Local(bobNotesTarget))],
            ct);

        // 5. Bob's install record contains ONLY Bob's bindings and NONE of Alice's brain/bindings.
        var bobRecord = await bobInstalls.Describe(received.AppId, ct);
        Assert.NotNull(bobRecord);
        var bobBindings = bobRecord.EffectiveBindings();
        var bobBinding = Assert.Single(bobBindings);
        Assert.Equal("notes", bobBinding.Slot);
        var local = Assert.IsType<BindingTarget.Local>(bobBinding.Target);
        Assert.Equal(bobNotesTarget, local.Neuron);
        Assert.Equal(bob.Brain, local.Neuron.Brain);
        Assert.NotEqual(alice.Brain, local.Neuron.Brain);
        Assert.DoesNotContain(bobBindings, b => b.Target is BindingTarget.Local l && l.Neuron.Brain == alice.Brain);
    }
}
