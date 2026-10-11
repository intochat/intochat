using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using DigitalBrain.Framework.Platform.Identity;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Circles.Tests.CircleBrainRooms;

namespace DigitalBrain.Circles.Tests;

// R126/R137: a circle is a brain of its own; each member's brain creates and lists its circles through the factory,
// and everything said, shared or paid happens in the circle brain.
public sealed class CircleFacts
{
    [Fact(Timeout = 120_000)]
    public async Task CreatingThroughTheFactoryBirthsTheCircleBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);

        var circle = await CreateIn(brain, alice, "friends");

        Assert.Equal(BrainKind.Circle, await brain.Services.GetRequiredService<IIdentityClient>().KindOf(circle));
        var state = await alice.In(circle, () => Circle(brain, circle).State());
        Assert.Equal(("friends", alice.AccountId), (state.Name, state.Creator));
        Assert.Equal([alice.AccountId], state.Members);
        Assert.Equal("Circle room", (await alice.In(circle, () => brain.Get<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(circle, "circleroom")).Read())).Manifest.Title);
        var once = await CreateIn(brain, alice, "family", "request-1");
        Assert.Equal(once, await CreateIn(brain, alice, "family", "request-1"));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheMembersIndexListsItsCircleBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var friends = await CreateIn(brain, alice, "friends");
        var family = await CreateIn(brain, alice, "family");

        await Join(brain, friends, alice, bob);

        Assert.Equal([new CircleEntry(family, "family"), new CircleEntry(friends, "friends")],
            (await CirclesOf(brain, alice)).OrderBy(entry => entry.Name, StringComparer.Ordinal));
        Assert.Equal([friends], (await CirclesOf(brain, bob)).Select(entry => entry.BrainId));
        // The shell's view of the same index: GET /identity/brains, each brain with its kind.
        var listed = await bob.Http.GetFromJsonAsync<System.Text.Json.JsonElement>("/identity/brains", ct);
        Assert.Contains(listed.EnumerateArray(), entry => entry.GetProperty("id").GetString() == friends && entry.GetProperty("kind").GetString() == "Circle");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(bob.BrainId, () => brain.Get<ICircleFactory>(ICircleFactory.Key(alice.BrainId)).Circles()));
    }

    [Fact(Timeout = 120_000)]
    public async Task SayingInACircleIsOneLineEveryMemberReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob, carol) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct), await brain.PersonAsync("carol", ct));
        var circle = await CreateIn(brain, alice, "friends");
        await Join(brain, circle, alice, bob);
        var chat = brain.Get<ICircleChat>(ICircleChat.Key(circle));

        await bob.In(circle, () => chat.Say("hello circle", "r-1"));
        await bob.In(circle, () => chat.Say("hello circle", "r-1"));

        var line = new CircleLine(bob.AccountId, "hello circle", 1);
        Assert.Equal(line, Assert.Single(await alice.In(circle, () => chat.Read(0))));
        Assert.Equal(line, Assert.Single(await bob.In(circle, () => chat.Read(0))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.In(circle, () => chat.Say("let me in")));
    }

    [Fact(Timeout = 120_000)]
    public async Task TheComputeShareChargesTheCircleCreator()
    {
        var ct = TestContext.Current.CancellationToken;
        // No module in this suite prices anything: a fixed price list stands in for the AI module's.
        await using var brain = await Start(ct, host => host.ConfigureServices(services => services.AddSingleton<IPriceList>(new FixedPriceList(100))));
        var (alice, bob, carol) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct), await brain.PersonAsync("carol", ct));
        var circle = await CreateIn(brain, alice, "friends");
        await Join(brain, circle, alice, bob);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(circle, () => Circle(brain, circle).SetComputeShare(10)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => alice.In(circle, () => Circle(brain, circle).SetComputeShare(ICircle.MaxComputeSharePercent + 1)));
        await alice.In(circle, () => Circle(brain, circle).SetComputeShare(10));
        var meter = brain.Services.GetRequiredService<ICostMeter>();

        // The creator has nothing to share yet, so a member's priced call is over the circle's share; a stranger cannot use it.
        await Assert.ThrowsAsync<BudgetExhaustedException>(() => bob.In(circle, () =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(ICircle.Key(circle)), "c-1", ct).AsTask()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.In(circle, () =>
            meter.RecordAsync(CostKind.Model, "gpt-5.1:input", 1m, "mtok", Funding.Circle(ICircle.Key(circle)), "c-2", ct).AsTask()));
    }

    // R126: inviting and accepting are a person's own, on the circle brain's human route; an app's script is refused.
    [Fact(Timeout = 120_000)]
    public async Task AScriptCannotAcceptOrInvite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        var circle = await CreateIn(brain, alice, "friends");
        var room = DigitalBrain.Apps.IApp.Key(circle, "circleroom");

        using (alice.AsAppIn(circle, room)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, circle).Invite(bob.AccountId)); }
        await alice.In(circle, () => Circle(brain, circle).Invite(bob.AccountId));
        using (bob.AsAppIn(circle, room)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Circle(brain, circle).Accept()); }

        Assert.Equal([bob.AccountId], (await alice.In(circle, () => Circle(brain, circle).State())).Invited);
        Assert.Equal([alice.AccountId, bob.AccountId], (await bob.In(circle, () => Circle(brain, circle).Accept())).Members);
    }

    [Fact]
    public void TheCircleContractsAliasEqualsTheSharedGrainTypeName()
        => Assert.Equal(DigitalBrain.Framework.CircleGrainType.Name, ((Orleans.AliasAttribute)Attribute.GetCustomAttribute(typeof(ICircle), typeof(Orleans.AliasAttribute))!).Alias);

    [Fact]
    public void ACircleNameIsLowercaseLettersDigitsAndDashes()
    {
        Assert.True(ICircle.IsValidName("friends-2"));
        Assert.False(ICircle.IsValidName("Friends"));
        Assert.False(ICircle.IsValidName(""));
        Assert.False(ICircle.IsValidName(new string('a', 41)));
    }

    // R124: sharing installs the sharer's own app into the circle brain through the feature loader, Origin = the
    // sharer's app, and lists nothing in the Marketplace.
    [Fact(Timeout = 120_000)]
    public async Task SharingAnAppInstallsItIntoTheCircleBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var alice = await brain.PersonAsync("alice", ct);
        await Own(brain, alice, "notes");
        var circle = await Create(brain, alice);

        await alice.In(circle, () => Circle(brain, circle).Share("notes", []));

        var shared = await alice.In(circle, () => brain.Get<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(circle, "notes")).Read());
        Assert.Equal("notes", shared.Manifest.Title);
        Assert.Equal(new DigitalBrain.Apps.AppOrigin(alice.AccountId, new DigitalBrain.Apps.AppSource.Brain(alice.AccountId, alice.BrainId), "notes", "1.0.0"), shared.Origin);
        Assert.Contains("notes", (await alice.In(circle, () => Circle(brain, circle).State())).SharedApps);
        Assert.Null(await alice.In(circle, () => brain.Get<DigitalBrain.Apps.IMarketplace>(DigitalBrain.Apps.IMarketplace.Key).Read(alice.PrincipalId, "notes")));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnyMemberMayShare()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct);
        var (alice, bob) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct));
        await Own(brain, bob, "diary");
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);

        await bob.In(circle, () => Circle(brain, circle).Share("diary", []));

        var shared = await alice.In(circle, () => brain.Get<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(circle, "diary")).Read());
        Assert.Equal((bob.AccountId, "diary"), (shared.Origin?.Publisher, shared.Manifest.Title));
    }

    [Fact(Timeout = 120_000)]
    public async Task ABundleThatDoesNotCompileIsNotShared()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start(ct, host => host.ConfigureServices(services =>
            services.AddSingleton<DigitalBrain.Apps.IScriptSandbox, MarkerSandbox>()));
        var alice = await brain.PersonAsync("alice", ct);
        await Own(brain, alice, "broken", files: new() { ["go.cs"] = NotCompiling });
        var circle = await Create(brain, alice);

        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.In(circle, () => Circle(brain, circle).Share("broken", [])));

        var app = await alice.In(circle, () => brain.Get<DigitalBrain.Apps.IApp>(DigitalBrain.Apps.IApp.Key(circle, "broken")).Read());
        Assert.Equal("", app.Manifest.Title);
        Assert.DoesNotContain("broken", (await alice.In(circle, () => Circle(brain, circle).State())).SharedApps);
    }


    // The member's own factory, called as the member in their own brain.
    private static async Task<string> CreateIn(ITestBrain brain, Person person, string name, string? requestId = null)
    {
        using (person.AsCaller()) { return await brain.Get<ICircleFactory>(ICircleFactory.Key(person.BrainId)).Create(name, requestId); }
    }

    private static async Task<CircleEntry[]> CirclesOf(ITestBrain brain, Person person)
    {
        using (person.AsCaller()) { return await brain.Get<ICircleFactory>(ICircleFactory.Key(person.BrainId)).Circles(); }
    }

    private sealed class FixedPriceList(long microsPerUnit) : IPriceList
    {
        public long? MicrosPerUnit(CostKind kind, string item) => microsPerUnit;
    }
}
