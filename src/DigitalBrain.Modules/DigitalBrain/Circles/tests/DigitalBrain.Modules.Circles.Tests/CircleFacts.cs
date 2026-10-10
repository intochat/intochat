using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Circles.Tests;

public sealed class CircleFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Team = ICircle.Key("alice", "team");

    private static Task<ModuleBrain> Start() => ModuleTest.Create().WithModule<CirclesModule>().StartAsync(Ct);

    private static void Act(string brain) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = brain,
        AccountId = "acct-" + brain,
        BrainId = brain,
        Kind = CallerKind.Platform,
        StampedBy = TrustedEdge.Platform,
    });

    private static ICircle Circle(ModuleBrain brain, string member) => brain.Get<ICircle>(ICircle.Key(member, "team"));

    private static ICircleChat Chat(ModuleBrain brain, string member) => brain.Get<ICircleChat>(ICircleChat.Key(ICircle.Key(member, "team")));

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
    public async Task AnInvitationIsHeardBeforeItIsRecordedAndPublishedToTheInvitedBrain()
    {
        await using var brain = await Start();
        Act("bob");
        var invited = Circle(brain, "bob");

        CircleInvited? heard = null;
        await foreach (var signal in brain.On<CircleInvited>(invited, async () =>
        {
            Act("alice");
            await Circle(brain, "alice").Invite("bob");
        }, TestContext.Current.CancellationToken))
        {
            heard = signal;
            break;
        }

        Assert.Equal(new("team", "alice"), (heard!.Name, heard.From));
        Act("alice");
        Assert.Equal(["bob"], (await Circle(brain, "alice").State()).Invited);
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
        var ownersChat = Chat(brain, "alice");

        CircleSpoke? heard = null;
        await foreach (var signal in brain.On<CircleSpoke>(ownersChat, async () =>
        {
            Act("bob");
            await Chat(brain, "bob").Say("hello circle");
        }, TestContext.Current.CancellationToken))
        {
            heard = signal;
            break;
        }

        Assert.Equal(("team", "bob", "hello circle", 1L), (heard!.Name, heard.Speaker, heard.Text, heard.Sequence));
        Act("alice");
        Assert.Equal(new CircleLine("bob", "hello circle", 1), Assert.Single(await ownersChat.Read(0)));
        Act("bob");
        Assert.Equal(new CircleLine("bob", "hello circle", 1), Assert.Single(await Chat(brain, "bob").Read(0)));
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
    public async Task AMembersCallFundedByTheCircleThrowsOnceItPassesTheOwnersShare()
    {
        await using var brain = await ModuleTest.Create().WithModule<CirclesModule>()
            .ConfigureSilo(silo => silo.Services.AddSingleton<IPriceList>(new FixedPriceList(100)))
            .StartAsync(Ct);
        await Joined(brain, "bob");
        Act("alice");
        await Circle(brain, "alice").SetComputeShare(10);
        Act("bob");
        var meter = brain.SiloServices.GetRequiredService<ICostMeter>();

        // The owner has nothing to share yet, so any priced call is over the circle's share.
        await Assert.ThrowsAsync<BudgetExhaustedException>(() =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(Team), "c-1", Ct).AsTask());
    }

    private sealed class FixedPriceList(long microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}
