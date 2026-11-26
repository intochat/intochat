using DigitalBrain;
using DigitalBrain.Access;

namespace IntoChat.OS.Tests.Unit.Framework.Callers;

public sealed class CallerRulesFacts
{
    private static readonly BrainId Brain = new("b1");
    private static readonly Actor.Person Alice = new("alice", "acc-1");
    private static EdgeAuthority Edge => TestEdge.Authority;

    public static TheoryData<string> Kinds => ["person", "relayed", "agent", "app", "module", "scheduler", "kernel", "bob", "none"];

    private static Caller? Of(string kind) => kind switch
    {
        "person" => Edge.Person("alice", "acc-1", Brain),
        "relayed" => Edge.RelayedPerson("alice", "acc-1", Brain),
        "agent" => Edge.Agent(Alice, Brain),
        "app" => Edge.App("assistant", Alice, Brain),
        "module" => Edge.Module("mod-1", Brain),
        "scheduler" => Edge.Scheduler(Brain),
        "kernel" => Edge.Kernel(Brain),
        "bob" => Edge.Person("bob", "acc-2", Brain),
        _ => null,
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnlyTheKernelIsTheKernel(string kind) => Assert.Equal(kind == "kernel", CallerRules.IsKernel(Of(kind)));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnlyAliceInPersonIsAliceDirectly(string kind) => Assert.Equal(kind == "person", CallerRules.IsDirectPerson(Of(kind), "alice"));

    [Theory]
    [MemberData(nameof(Kinds))]
    public void OnlyAnAppIsAnAppAndCarriesItsKey(string kind)
    {
        var isApp = CallerRules.IsApp(Of(kind), out var key);

        Assert.Equal(kind == "app", isApp);
        Assert.Equal(kind == "app" ? "assistant" : null, key);
    }

    [Fact]
    public void Day0GuestOnOwnTryBrain_IsGuest_NotThirdPartyApp()
    {
        var tryBrain = new BrainId("try-abc123");
        var guest = Edge.Guest("try-abc123", "try-abc123", tryBrain);

        Assert.True(CallerRules.IsGuest(guest));
        Assert.False(CallerRules.IsApp(guest, out var key));
        Assert.Null(key);
    }

    [Fact]
    public void SandboxMiniAppInsideTryBrain_RemainsAnApp_NotHumanGuest()
    {
        var tryBrain = new BrainId("try-abc123");
        var miniApp = Edge.App("try-abc123/apps/untrusted", new Actor.Person("try-abc123", "try-abc123"), tryBrain);

        Assert.False(CallerRules.IsGuest(miniApp));
        Assert.True(CallerRules.IsApp(miniApp, out var key));
        Assert.Equal("try-abc123/apps/untrusted", key);
    }

    [Fact]
    public void CrossBrainOrNonTryGuest_IsNotTreatedAsOwnBrainGuest()
    {
        var crossBrainGuest = Edge.Guest("try-aaa", "try-aaa", new BrainId("try-bbb"));
        var nonTryGuest = Edge.Guest("owner", "owner", new BrainId("owner"));

        Assert.False(CallerRules.IsGuest(crossBrainGuest));
        Assert.True(CallerRules.IsApp(crossBrainGuest, out _));
        Assert.False(CallerRules.IsGuest(nonTryGuest));
        Assert.True(CallerRules.IsApp(nonTryGuest, out _));
    }
}
