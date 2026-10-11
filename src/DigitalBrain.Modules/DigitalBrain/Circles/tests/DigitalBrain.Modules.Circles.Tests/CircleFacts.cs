using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace DigitalBrain.Circles.Tests;

public sealed class CircleFacts
{
    private static async Task<Room> Start(CancellationToken ct, Action<IServiceCollection>? services = null)
    {
        var builder = TestBrain.Create().WithModule<CirclesModule>();
        if (services is not null) { builder = builder.ConfigureServices(services); }
        var brain = await builder.StartAsync(ct);
        return new(brain, await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct),
            await brain.PersonAsync("carol", ct), await brain.PersonAsync("dave", ct));
    }

    [Fact]
    public async Task AnAcceptedInvitationMakesTheInvitedBrainAMemberOnBothBrains()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);

        await alice.As(() => room.Circle(alice).Invite(bob.BrainId));
        Assert.Equal(alice.BrainId, (await bob.As(() => room.Circle(bob).State())).InvitedBy);
        var joined = await bob.As(() => room.Circle(bob).Accept());

        Assert.Equal([alice.BrainId, bob.BrainId], joined.Members);
        Assert.Equal(alice.BrainId, joined.Owner);
        Assert.Equal([alice.BrainId, bob.BrainId], await bob.As(() => room.Circle(bob).Members()));
        var owners = await alice.As(() => room.Circle(alice).State());
        Assert.Equal([alice.BrainId, bob.BrainId], owners.Members);
        Assert.Empty(owners.Invited);
    }

    [Fact]
    public async Task AnInvitationIsPublishedToTheInvitedBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var room = await Start(ct);
        var (alice, bob) = (room.Alice, room.Bob);
        await using var invited = await bob.As(() => room.Brain.Observe<CircleInvited>(room.Circle(bob), ct));

        await alice.As(() => room.Circle(alice).Invite(bob.BrainId));

        var heard = await invited.NextAsync(ct: ct);
        Assert.Equal(("team", alice.BrainId), (heard.Name, heard.From));
        Assert.Equal([bob.BrainId], (await alice.As(() => room.Circle(alice).State())).Invited);
    }

    [Fact]
    public async Task AnInvitationToABrainWithNoCopyIsTouchedOnItsIndexAndListedAsInvited()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var room = await Start(ct);
        var (alice, bob) = (room.Alice, room.Bob);
        await using var touches = await bob.As(() => room.Brain.Observe<CircleTouched>(room.Index(bob), ct));

        await alice.As(() => room.Circle(alice).Invite(bob.BrainId));

        var touched = await touches.NextAsync(ct: ct);
        Assert.Equal(("team", alice.BrainId, "invited"), (touched.Name, touched.Owner, touched.Kind));
        Assert.Equal(new CircleEntry("team", alice.BrainId, "invited"), Assert.Single(await bob.As(() => room.Index(bob).Names())));
        Assert.Equal(new CircleEntry("team", alice.BrainId, "owner"), Assert.Single(await alice.As(() => room.Index(alice).Names())));
    }

    [Fact]
    public async Task ALineIsTouchedAsSpokeOnEveryMembersIndexAndALeaverDropsOutOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var room = await Start(ct);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);
        Assert.Equal(new CircleEntry("team", alice.BrainId, "member"), Assert.Single(await bob.As(() => room.Index(bob).Names())));
        await using var onBob = await bob.As(() => room.Brain.Observe<CircleTouched>(room.Index(bob), ct));
        await using var onAlice = await alice.As(() => room.Brain.Observe<CircleTouched>(room.Index(alice), ct));

        await alice.As(() => room.Chat(alice).Say("hi"));
        var spokeOnBob = await onBob.NextAsync(t => t.Kind == "spoke", ct);
        await bob.As(() => room.Chat(bob).Say("hello"));
        var spokeOnAlice = await onAlice.NextAsync(t => t.Kind == "spoke", ct);

        Assert.Equal(("team", alice.BrainId), (spokeOnBob.Name, spokeOnBob.Owner));
        Assert.Equal("spoke", spokeOnAlice.Kind);
        await bob.As(() => room.Circle(bob).Leave(bob.BrainId));
        Assert.Empty(await bob.As(() => room.Index(bob).Names()));
    }

    [Fact]
    public async Task ASubscriberToTheIndexAfterATouchIsSeededWithIt()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await alice.As(() => room.Circle(alice).Invite(bob.BrainId));
        var index = room.Index(bob).AsReference<IRetainsSignals>();

        var retained = await bob.As(() => index.Retained(nameof(CircleTouched), 0));

        var touch = Assert.IsType<CircleTouched>(Assert.Single(retained).Signal);
        Assert.Equal(("team", alice.BrainId, "invited"), (touch.Name, touch.Owner, touch.Kind));
        Assert.EndsWith(ICircles.Key(bob.BrainId), touch.Publisher.Name, StringComparison.Ordinal);
        Assert.Empty(await bob.As(() => index.Retained(nameof(CircleTouched), retained[0].Sequence)));
    }

    [Fact]
    public async Task OnlyAnInvitedBrainCanJoin()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        await room.Alice.As(() => room.Circle(room.Alice).Invite(room.Bob.BrainId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => room.Carol.As(() => room.Circle(room.Carol).Accept()));
    }

    [Fact]
    public async Task TwoOwnersMayEachRunACircleCalledTeamWithTheSameMember()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob, dave) = (room.Alice, room.Bob, room.Dave);
        await alice.As(() => room.Circle(alice).Invite(bob.BrainId));
        await dave.As(() => room.Circle(dave, dave).Invite(bob.BrainId));

        await bob.As(() => room.Circle(bob).Accept());
        await bob.As(() => room.Circle(bob, dave).Accept());

        Assert.Equal([alice.BrainId, bob.BrainId], await bob.As(() => room.Circle(bob).Members()));
        Assert.Equal([dave.BrainId, bob.BrainId], await bob.As(() => room.Circle(bob, dave).Members()));
    }

    [Fact]
    public async Task InvitationsMadeAtTheSameTimeAreAllKept()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var owners = room.Circle(room.Alice);

        await room.Alice.As(() => Task.WhenAll(owners.Invite(room.Bob.BrainId), owners.Invite(room.Carol.BrainId), owners.Invite(room.Dave.BrainId)));

        Assert.Equal(new[] { room.Bob.BrainId, room.Carol.BrainId, room.Dave.BrainId }.Order(),
            (await room.Alice.As(() => owners.State())).Invited.Order());
    }

    [Fact]
    public async Task AFormerMemberCanBeInvitedAgainRightAway()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);
        await alice.As(async () =>
        {
            await room.Circle(alice).Leave(bob.BrainId);
            await room.Circle(alice).Invite(bob.BrainId);
        });

        var joined = await bob.As(() => room.Circle(bob).Accept());

        Assert.Equal([alice.BrainId, bob.BrainId], joined.Members);
    }

    [Fact]
    public async Task ARefusedInvitationLeavesTheOwnersRecordAsItWasAndPublishesNothingTwice()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var owners = room.Circle(room.Alice);
        using var caller = room.Alice.AsCaller();

        await Assert.ThrowsAsync<ArgumentException>(() => owners.Invite("not/a-brain"));
        await owners.Invite(room.Bob.BrainId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owners.Invite(room.Bob.BrainId));

        Assert.Equal([room.Bob.BrainId], (await owners.State()).Invited);
        Assert.Equal(1, (await owners.State()).Version);
    }

    [Fact]
    public async Task AMemberCanLeaveAndTheOwnerCanRemoveAMember()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob, carol) = (room.Alice, room.Bob, room.Carol);
        await room.Joined(bob);
        await room.Joined(carol);

        await bob.As(() => room.Circle(bob).Leave(bob.BrainId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => bob.As(() => room.Circle(bob).State()));
        Assert.Equal([alice.BrainId, carol.BrainId], await carol.As(() => room.Circle(carol).Members()));

        await alice.As(() => room.Circle(alice).Leave(carol.BrainId));
        Assert.Equal([alice.BrainId], await alice.As(() => room.Circle(alice).Members()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => carol.As(() => room.Circle(carol).State()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.As(() => room.Circle(alice).Leave(alice.BrainId)));
    }

    [Fact]
    public async Task OnlyTheOwnerChangesWhatTheCircleShares()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);

        await alice.As(() => room.Circle(alice).Share("notes"));
        Assert.Equal(["notes"], (await bob.As(() => room.Circle(bob).State())).SharedApps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.As(() => room.Circle(bob).Share("diary")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.As(() => room.Circle(bob).Invite(room.Carol.BrainId)));
        await alice.As(() => room.Circle(alice).Unshare("notes"));
        Assert.Empty((await bob.As(() => room.Circle(bob).State())).SharedApps);
    }

    [Fact]
    public async Task ALineSaidByAMemberIsReadableFromBothBrainsAndPublishedAsCircleSpoke()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var room = await Start(ct);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);
        var ownersChat = room.Chat(alice);
        await using var spoke = await alice.As(() => room.Brain.Observe<CircleSpoke>(ownersChat, ct));

        await bob.As(() => room.Chat(bob).Say("hello circle"));

        var heard = await spoke.NextAsync(ct: ct);
        Assert.Equal(("team", bob.BrainId, "hello circle", 1L), (heard.Name, heard.Speaker, heard.Text, heard.Sequence));
        Assert.Equal(new CircleLine(bob.BrainId, "hello circle", 1), Assert.Single(await alice.As(() => ownersChat.Read(0))));
        Assert.Equal(new CircleLine(bob.BrainId, "hello circle", 1), Assert.Single(await bob.As(() => room.Chat(bob).Read(0))));
    }

    [Fact]
    public void TheCircleContractsAliasEqualsTheSharedGrainTypeName()
    {
        Assert.Equal(DigitalBrain.Framework.CircleGrainType.Name, ((Orleans.AliasAttribute)Attribute.GetCustomAttribute(typeof(ICircle), typeof(Orleans.AliasAttribute))!).Alias);
    }

    [Fact]
    public async Task SayingTwiceUnderOneRequestIdYieldsOneLine()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);

        await alice.As(async () =>
        {
            await room.Chat(alice).Say("hi", "request-1");
            await room.Chat(alice).Say("hi", "request-1");
        });
        await bob.As(async () =>
        {
            await room.Chat(bob).Say("yo", "request-1");
            await room.Chat(bob).Say("yo", "request-1");
        });

        CircleLine[] expected = [new(alice.BrainId, "hi", 1), new(bob.BrainId, "yo", 2)];
        Assert.Equal(expected, await bob.As(() => room.Chat(bob).Read(0)));
        Assert.Equal(expected, await alice.As(() => room.Chat(alice).Read(0)));
    }

    [Fact]
    public async Task LinesFromBothBrainsAreNumberedOnceInTheOwnersOrder()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);

        await alice.As(() => room.Chat(alice).Say("one"));
        await bob.As(() => room.Chat(bob).Say("two"));
        await alice.As(() => room.Chat(alice).Say("three"));

        CircleLine[] expected = [new(alice.BrainId, "one", 1), new(bob.BrainId, "two", 2), new(alice.BrainId, "three", 3)];
        Assert.Equal(expected, await alice.As(() => room.Chat(alice).Read(0)));
        Assert.Equal(expected, await bob.As(() => room.Chat(bob).Read(0)));
        Assert.Equal(expected[2..], await bob.As(() => room.Chat(bob).Read(2)));
    }

    [Fact]
    public async Task OnlyAMemberCanSay()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        await room.Alice.As(() => room.Circle(room.Alice).Invite(room.Bob.BrainId));

        await Assert.ThrowsAsync<InvalidOperationException>(() => room.Bob.As(() => room.Chat(room.Bob).Say("before accepting")));
    }

    [Fact]
    public async Task ABrainActsOnlyOnItsOwnCopiesAndNoCallerReachesTheDeliveryBetweenCopies()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var (alice, bob) = (room.Alice, room.Bob);
        await room.Joined(bob);

        using (room.Carol.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(alice).Share("notes"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(bob).State());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(bob).Leave(bob.BrainId));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Chat(bob).Say("as bob"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Chat(alice).Read(0));
        }

        // Even the member itself, from outside the cluster, cannot make a copy believe anything.
        using (bob.AsCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(alice).Joined());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(alice).Left());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(alice).StateForCaller());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(bob).Invited());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Circle(alice).Roster());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Chat(alice).Relay("forged"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Chat(alice).Pull(0));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Index(bob).Touch("team", alice.BrainId, "changed", "owner"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                room.Brain.Services.GetRequiredService<IGrainFactory>().GetGrain<ICircleShares>(room.Team).Configure(25, [alice.BrainId, bob.BrainId, "mallory"]));
        }
    }

    [Fact]
    public void ACircleNameIsLowercaseLettersDigitsAndDashes()
    {
        Assert.Equal("alice/circles/team-2", ICircle.Key("alice", "team-2"));
        Assert.Equal("bob/circles/team@alice", ICircle.Key("bob", "team", "alice"));
        foreach (var name in new[] { "", "a/b", "x@y", "Team", "has space", new string('a', 41) })
        { Assert.Throws<ArgumentException>(() => ICircle.Key("alice", name)); }
    }

    [Fact]
    public async Task AComputeShareOverTheQuarterTheKernelKeepsIsRefused()
    {
        await using var room = await Start(TestContext.Current.CancellationToken);
        var circle = room.Circle(room.Alice);
        using var caller = room.Alice.AsCaller();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => circle.SetComputeShare(30));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => circle.SetComputeShare(-1));
        await circle.SetComputeShare(25);
        Assert.Equal(25, (await circle.State()).ComputeSharePercent);
    }

    [Fact]
    public async Task AMembersCallFundedByTheCircleThrowsOnceItPassesTheShareAndANonMemberCannotUseIt()
    {
        var ct = TestContext.Current.CancellationToken;
        // No module in this suite prices anything: a fixed price list stands in for the AI module's.
        await using var room = await Start(ct, services => services.AddSingleton<IPriceList>(new FixedPriceList(100)));
        await room.Joined(room.Bob);
        await room.Alice.As(() => room.Circle(room.Alice).SetComputeShare(10));
        var meter = room.Brain.Services.GetRequiredService<ICostMeter>();

        // The brain that spends is the one the call's trace names: each person's caller scope begins its own trace.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => room.Carol.As(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(room.Team), "c-2", ct).AsTask()));
        // The owner has nothing to share yet, so any priced call is over the circle's share.
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => room.Bob.As(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(room.Team), "c-1", ct).AsTask()));
    }

    private sealed record Room(ITestBrain Brain, Person Alice, Person Bob, Person Carol, Person Dave) : IAsyncDisposable
    {
        public string Team => ICircle.Key(Alice.BrainId, "team");

        // The copy `holder` holds of the circle `owner` runs (Alice by default).
        public ICircle Circle(Person holder, Person? owner = null, string name = "team")
            => Brain.Get<ICircle>(ICircle.Key(holder.BrainId, name, (owner ?? Alice).BrainId));

        public ICircleChat Chat(Person holder) => Brain.Get<ICircleChat>(ICircleChat.Key(ICircle.Key(holder.BrainId, "team", Alice.BrainId)));

        public ICircles Index(Person holder) => Brain.Get<ICircles>(ICircles.Key(holder.BrainId));

        public async Task Joined(Person member)
        {
            await Alice.As(() => Circle(Alice).Invite(member.BrainId));
            await member.As(() => Circle(member).Accept());
        }

        public ValueTask DisposeAsync() => Brain.DisposeAsync();
    }

    private sealed class FixedPriceList(long microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}

internal static class Acting
{
    public static async Task<T> As<T>(this Person person, Func<Task<T>> call)
    {
        using var caller = person.AsCaller();
        return await call();
    }

    public static async Task As(this Person person, Func<Task> call)
    {
        using var caller = person.AsCaller();
        await call();
    }
}
