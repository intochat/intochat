using System.Text.Json;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

public sealed class KeptScenariosFacts
{
    private static Scenario S(string name, string then)
        => new(name, JsonElement.Parse("{}"), ["tick 6m"], [then]);

    private static Dictionary<string, string> Files(params Scenario[] scenarios)
        => new() { ["scenarios.json"] = JsonSerializer.Serialize(scenarios, JsonSerializerOptions.Web) };

    [Fact] public void WithNothingDeployedTheAuthoredSetIsTheSet()
        => Assert.Equal(["a"], KeptScenarios.Merge(null, [S("a", "state x.watch Unseen = 1")], "make x").Select(s => s.Name));

    [Fact] public void EarlierScenariosComeFirstAndNewOnesFollow()
        => Assert.Equal(["a", "b", "c"], KeptScenarios.Merge(Files(S("a", "1"), S("b", "2")), [S("c", "3")], "change x.watch: c").Select(s => s.Name));

    [Fact] public void AnExactRepeatIsNotAddedTwice()
        => Assert.Equal(["a"], KeptScenarios.Merge(Files(S("a", "1")), [S("a", "1")], "change").Select(s => s.Name));

    [Fact] public void ARepeatWithNoGivenIsStillARepeat()   // an absent given is default(JsonElement), which cannot be serialized
    {
        var kept = new Dictionary<string, string> { ["scenarios.json"] = """[{"name": "a", "when": ["publish x.seen"], "then": ["state x.watch Unseen = 0"]}]""" };
        Assert.Single(KeptScenarios.Merge(kept, [new Scenario("a", default, ["publish x.seen"], ["state x.watch Unseen = 0"])], "change"));
    }

    [Fact]   // Review Focus 4
    public void ASameNameWithOtherContentIsKeptBesideTheEarlierOne()
    {
        var merged = KeptScenarios.Merge(Files(S("a", "state x.watch Unseen = 1")), [S("a", "state x.watch Unseen = 2")], "change");
        Assert.Equal(["a", "a (2)"], merged.Select(s => s.Name));
        Assert.Equal("state x.watch Unseen = 1", merged[0].Then[0]);
    }

    [Fact] public void SupersedesMarksTheEarlierOneWithTheChangeSentence()
    {
        var merged = KeptScenarios.Merge(Files(S("a", "1"), S("b", "2")), [S("c", "3") with { Supersedes = "a" }], "  change x.watch: the opposite  ");
        Assert.Equal(["a", "b", "c"], merged.Select(s => s.Name));
        Assert.Equal("change x.watch: the opposite", merged[0].Superseded);
        Assert.Null(merged[1].Superseded);
        Assert.Equal(["b", "c"], KeptScenarios.Live(merged).Select(s => s.Name));
    }

    [Fact] public void SupersedingAnUnknownNameIsRefused()
        => Assert.Throws<InvalidDataException>(() => KeptScenarios.Merge(Files(S("a", "1")), [S("c", "3") with { Supersedes = "zz" }], "change"));

    [Fact] public void AKeptFileThatDoesNotReadIsRefusedNotDropped()
        => Assert.Throws<InvalidDataException>(() => KeptScenarios.Earlier(new Dictionary<string, string> { ["scenarios.json"] = "not json" }));

    [Fact] public void ScenariosWrittenBeforeTheFieldsStillRead()
    {
        var old = """[{"name": "a", "given": {}, "when": ["tick 6m"], "then": ["signal x.polled"]}]""";
        var read = KeptScenarios.Earlier(new Dictionary<string, string> { ["scenarios.json"] = old });
        Assert.Null(read[0].Supersedes);
        Assert.Null(read[0].Superseded);
        Assert.DoesNotContain("supersede", JsonSerializer.Serialize(read, JsonSerializerOptions.Web), StringComparison.Ordinal);
    }
}
