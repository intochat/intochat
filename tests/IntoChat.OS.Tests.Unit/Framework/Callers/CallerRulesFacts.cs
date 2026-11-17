using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Framework;

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
}
