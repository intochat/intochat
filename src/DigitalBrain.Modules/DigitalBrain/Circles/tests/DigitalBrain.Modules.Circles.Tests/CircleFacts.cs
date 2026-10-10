using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Circles.Tests;

public sealed class CircleFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Team = ICircle.Key("alice", "team");

    private static async Task<ModuleBrain> Start(Action<ISiloBuilder>? configure = null)
    {
        var builder = ModuleTest.Create().WithModule<CirclesModule>();
        if (configure is not null) { builder = builder.ConfigureSilo(configure); }
        var brain = await builder.StartAsync(Ct);
        foreach (var name in new[] { "alice", "bob", "carol", "dave" })
        { await brain.Get<IBrain>(name).Create(new Owner("acct-" + name)); }
        return brain;
    }

    private static void Act(string brain, string? principal = null) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = principal ?? brain,
        AccountId = "acct-" + brain,
        BrainId = brain,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });

    // The copy a brain holds of the circle `owner` runs.
    private static ICircle Circle(ModuleBrain brain, string holder, string owner = "alice", string name = "team")
        => brain.Get<ICircle>(ICircle.Key(holder, name, owner));

    private static ICircleChat Chat(ModuleBrain brain, string holder, string owner = "alice")
        => brain.Get<ICircleChat>(ICircleChat.Key(ICircle.Key(holder, "team", owner)));

    private static async Task Joined(ModuleBrain brain, string member)
    {
        Act("alice");
        await Circle(brain, "alice").Invite(member);
        Act(member);
        await Circle(brain, member).Accept();
    }

    [Fact]
    public async Task AnAcceptedInvitationMakesTheInvitedBrainAMemberOnBothBrains()
    {
        await using var brain = await Start();

        Act("alice");
        await Circle(brain, "alice").Invite("bob");
        Act("bob");
        Assert.Equal("alice", (await Circle(brain, "bob").State()).InvitedBy);
        var joined = await Circle(brain, "bob").Accept();

        Assert.Equal(["alice", "bob"], joined.Members);
        Assert.Equal("alice", joined.Owner);
        Assert.Equal(["alice", "bob"], await Circle(brain, "bob").Members());
        Act("alice");
        var owners = await Circle(brain, "alice").State();
        Assert.Equal(["alice", "bob"], owners.Members);
        Assert.Empty(owners.Invited);
    }

    [Fact]
    public async Task AnInvitationIsPublishedToTheInvitedBrain()
    {
        await using var brain = await Start();
        Act("bob");

        CircleInvited? heard = null;
        await foreach (var signal in brain.On<CircleInvited>(Circle(brain, "bob"), async () =>
        {
            Act("alice");
            await Circle(brain, "alice").Invite("bob");
        }, TestContext.Current.CancellationToken))
        {
            heard = signal;
            break;
        }

        Assert.Equal(("team", "alice"), (heard!.Name, heard.From));
        Act("alice");
        Assert.Equal(["bob"], (await Circle(brain, "alice").State()).Invited);
    }

    private static ICircles Index(ModuleBrain brain, string holder) => brain.Get<ICircles>(ICircles.Key(holder));

    [Fact]
    public async Task AnInvitationToABrainWithNoCopyIsTouchedOnItsIndexAndListedAsInvited()
    {
        await using var brain = await Start();
        Act("bob");

        CircleTouched? touched = null;
        await foreach (var signal in brain.On<CircleTouched>(Index(brain, "bob"), async () =>
        {
            Act("alice");
            await Circle(brain, "alice").Invite("bob");
        }, TestContext.Current.CancellationToken))
        {
            touched = signal;
            break;
        }

        Assert.Equal(("team", "alice", "invited"), (touched!.Name, touched.Owner, touched.Kind));
        Act("bob");
        Assert.Equal(new CircleEntry("team", "alice", "invited"), Assert.Single(await Index(brain, "bob").Names()));
        Act("alice");
        Assert.Equal(new CircleEntry("team", "alice", "owner"), Assert.Single(await Index(brain, "alice").Names()));
    }

    [Fact]
    public async Task ALineIsTouchedAsSpokeOnEveryMembersIndexAndALeaverDropsOutOfIt()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");
        Act("bob");
        Assert.Equal(new CircleEntry("team", "alice", "member"), Assert.Single(await Index(brain, "bob").Names()));

        CircleTouched? onBob = null;
        await foreach (var signal in brain.On<CircleTouched>(Index(brain, "bob"), async () =>
        {
            Act("alice");
            await Chat(brain, "alice").Say("hi");
        }, TestContext.Current.CancellationToken))
        {
            if (signal.Kind != "spoke") { continue; }
            onBob = signal;
            break;
        }
        Assert.Equal(("team", "alice"), (onBob!.Name, onBob.Owner));

        CircleTouched? onAlice = null;
        await foreach (var signal in brain.On<CircleTouched>(Index(brain, "alice"), async () =>
        {
            Act("bob");
            await Chat(brain, "bob").Say("hello");
        }, TestContext.Current.CancellationToken))
        {
            if (signal.Kind != "spoke") { continue; }
            onAlice = signal;
            break;
        }
        Assert.Equal("spoke", onAlice!.Kind);

        Act("bob");
        await Circle(brain, "bob").Leave("bob");
        Assert.Empty(await Index(brain, "bob").Names());
    }

    [Fact]
    public async Task ASubscriberToTheIndexAfterATouchIsSeededWithIt()
    {
        await using var brain = await Start();
        Act("alice");
        await Circle(brain, "alice").Invite("bob");
        Act("bob");

        var retained = await Index(brain, "bob").AsReference<IRetainsSignals>().Retained(nameof(CircleTouched), 0);

        var touch = Assert.IsType<CircleTouched>(Assert.Single(retained).Signal);
        Assert.Equal(("team", "alice", "invited"), (touch.Name, touch.Owner, touch.Kind));
        Assert.EndsWith(ICircles.Key("bob"), touch.Publisher.Name, StringComparison.Ordinal);
        Assert.Empty(await Index(brain, "bob").AsReference<IRetainsSignals>().Retained(nameof(CircleTouched), retained[0].Sequence));
    }

    [Fact]
    public async Task OnlyAnInvitedBrainCanJoin()
    {
        await using var brain = await Start();
        Act("alice");
        await Circle(brain, "alice").Invite("bob");

        Act("carol");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Circle(brain, "carol").Accept());
    }

    [Fact]
    public async Task TwoOwnersMayEachRunACircleCalledTeamWithTheSameMember()
    {
        await using var brain = await Start();
        Act("alice");
        await Circle(brain, "alice").Invite("bob");
        Act("dave");
        await Circle(brain, "dave", "dave").Invite("bob");
        Act("bob");
        await Circle(brain, "bob").Accept();
        await Circle(brain, "bob", "dave").Accept();

        Assert.Equal(["alice", "bob"], await Circle(brain, "bob").Members());
        Assert.Equal(["dave", "bob"], await Circle(brain, "bob", "dave").Members());
    }

    [Fact]
    public async Task InvitationsMadeAtTheSameTimeAreAllKept()
    {
        await using var brain = await Start();
        Act("alice");
        var owners = Circle(brain, "alice");

        await Task.WhenAll(owners.Invite("bob"), owners.Invite("carol"), owners.Invite("dave"));

        Assert.Equal(["bob", "carol", "dave"], (await owners.State()).Invited.Order());
    }

    [Fact]
    public async Task AFormerMemberCanBeInvitedAgainRightAway()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");
        Act("alice");
        await Circle(brain, "alice").Leave("bob");
        await Circle(brain, "alice").Invite("bob");

        Act("bob");
        var joined = await Circle(brain, "bob").Accept();

        Assert.Equal(["alice", "bob"], joined.Members);
    }

    [Fact]
    public async Task ARefusedInvitationLeavesTheOwnersRecordAsItWasAndPublishesNothingTwice()
    {
        await using var brain = await Start();
        Act("alice");
        var owners = Circle(brain, "alice");

        await Assert.ThrowsAsync<ArgumentException>(() => owners.Invite("not/a-brain"));
        await owners.Invite("bob");
        await Assert.ThrowsAsync<InvalidOperationException>(() => owners.Invite("bob"));

        Assert.Equal(["bob"], (await owners.State()).Invited);
        Assert.Equal(1, (await owners.State()).Version);
    }

    [Fact]
    public async Task AMemberCanLeaveAndTheOwnerCanRemoveAMember()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");
        await Joined(brain, "carol");

        Act("bob");
        await Circle(brain, "bob").Leave("bob");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Circle(brain, "bob").State());
        Act("carol");
        Assert.Equal(["alice", "carol"], await Circle(brain, "carol").Members());

        Act("alice");
        await Circle(brain, "alice").Leave("carol");
        Assert.Equal(["alice"], await Circle(brain, "alice").Members());
        Act("carol");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Circle(brain, "carol").State());
        Act("alice");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Circle(brain, "alice").Leave("alice"));
    }

    [Fact]
    public async Task OnlyTheOwnerChangesWhatTheCircleShares()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");

        Act("alice");
        await Circle(brain, "alice").Share("notes");
        Act("bob");
        Assert.Equal(["notes"], (await Circle(brain, "bob").State()).SharedApps);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Circle(brain, "bob").Share("diary"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Circle(brain, "bob").Invite("carol"));
        Act("alice");
        await Circle(brain, "alice").Unshare("notes");
        Act("bob");
        Assert.Empty((await Circle(brain, "bob").State()).SharedApps);
    }

    [Fact]
    public async Task ALineSaidByAMemberIsReadableFromBothBrainsAndPublishedAsCircleSpoke()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");
        Act("alice");
        var ownersChat = Chat(brain, "alice", "alice");

        CircleSpoke? heard = null;
        await foreach (var signal in brain.On<CircleSpoke>(ownersChat, async () =>
        {
            Act("bob", principal: "bobs-assistant");
            await Chat(brain, "bob").Say("hello circle");
        }, TestContext.Current.CancellationToken))
        {
            heard = signal;
            break;
        }

        // The speaker is the brain the kernel says spoke, not the principal inside it.
        Assert.Equal(("team", "bob", "hello circle", 1L), (heard!.Name, heard.Speaker, heard.Text, heard.Sequence));
        Act("alice");
        Assert.Equal(new CircleLine("bob", "hello circle", 1), Assert.Single(await ownersChat.Read(0)));
        Act("bob");
        Assert.Equal(new CircleLine("bob", "hello circle", 1), Assert.Single(await Chat(brain, "bob").Read(0)));
    }

    [Fact]
    public async Task SayingTwiceUnderOneRequestIdYieldsOneLine()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");

        Act("alice");
        await Chat(brain, "alice").Say("hi", "request-1");
        await Chat(brain, "alice").Say("hi", "request-1");
        Act("bob");
        await Chat(brain, "bob").Say("yo", "request-1");
        await Chat(brain, "bob").Say("yo", "request-1");

        CircleLine[] expected = [new("alice", "hi", 1), new("bob", "yo", 2)];
        Assert.Equal(expected, await Chat(brain, "bob").Read(0));
        Act("alice");
        Assert.Equal(expected, await Chat(brain, "alice").Read(0));
    }

    [Fact]
    public async Task LinesFromBothBrainsAreNumberedOnceInTheOwnersOrder()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");

        Act("alice");
        await Chat(brain, "alice").Say("one");
        Act("bob");
        await Chat(brain, "bob").Say("two");
        Act("alice");
        await Chat(brain, "alice").Say("three");

        CircleLine[] expected = [new("alice", "one", 1), new("bob", "two", 2), new("alice", "three", 3)];
        Assert.Equal(expected, await Chat(brain, "alice").Read(0));
        Act("bob");
        Assert.Equal(expected, await Chat(brain, "bob").Read(0));
        Assert.Equal(expected[2..], await Chat(brain, "bob").Read(2));
    }

    [Fact]
    public async Task OnlyAMemberCanSay()
    {
        await using var brain = await Start();
        Act("alice");
        await Circle(brain, "alice").Invite("bob");

        Act("bob");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Chat(brain, "bob").Say("before accepting"));
    }

    [Fact]
    public async Task ABrainActsOnlyOnItsOwnCopiesAndNoCallerReachesTheDeliveryBetweenCopies()
    {
        await using var brain = await Start();
        await Joined(brain, "bob");
        Act("carol");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "alice").Share("notes"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "bob").State());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "bob").Leave("bob"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(brain, "bob").Say("as bob"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(brain, "alice").Read(0));

        // Even the member itself, from outside the cluster, cannot make a copy believe anything.
        Act("bob");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "alice").Joined());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "alice").Left());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "alice").StateForCaller());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "bob").Invited());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, "alice").Roster());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(brain, "alice").Relay("forged"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Chat(brain, "alice").Pull(0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Index(brain, "bob").Touch("team", "alice", "changed", "owner"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            brain.Grains.GetGrain<ICircleShares>(Team).Configure(25, ["alice", "bob", "mallory"]));
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
        await using var brain = await Start();
        Act("alice");
        var circle = Circle(brain, "alice");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => circle.SetComputeShare(30));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => circle.SetComputeShare(-1));
        await circle.SetComputeShare(25);
        Assert.Equal(25, (await circle.State()).ComputeSharePercent);
    }

    [Fact]
    public async Task AMembersCallFundedByTheCircleThrowsOnceItPassesTheShareAndANonMemberCannotUseIt()
    {
        await using var brain = await Start(silo => silo.Services.AddSingleton<IPriceList>(new FixedPriceList(100)));
        await Joined(brain, "bob");
        Act("alice");
        await Circle(brain, "alice").SetComputeShare(10);
        var meter = brain.SiloServices.GetRequiredService<ICostMeter>();

        // The brain that spends is the one the call's trace names, so each call runs in its own brain's trace.
        using (TraceContext.Enter(TraceContext.Begin("acct-carol", "carol", new NeuronId("carol/edge"))))
        {
            Act("carol");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(Team), "c-2", Ct).AsTask());
        }
        // The owner has nothing to share yet, so any priced call is over the circle's share.
        using (TraceContext.Enter(TraceContext.Begin("acct-bob", "bob", new NeuronId("bob/edge"))))
        {
            Act("bob");
            await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
                meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(Team), "c-1", Ct).AsTask());
        }
    }

    private sealed class FixedPriceList(long microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}
