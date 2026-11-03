using DigitalBrain;
using DigitalBrain.Rules;
using IntoChat.OS.Modules.Flame.Scene;

namespace IntoChat.OS.Modules.Flame.Tests.Rules;

public sealed class SceneRulesFacts(FlameFixture fixture) : IClassFixture<FlameFixture>
{
    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(SceneNeuron).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(Rule)))
            .Select(CreateDefault)
            .ToArray();

        Assert.Single(rules);
        Assert.All(rules, r => Assert.StartsWith("flame.", r.Name, StringComparison.Ordinal));
        Assert.Equal(rules.Length, rules.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
    }

    private static Rule CreateDefault(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return (Rule)ctor.Invoke(args);
    }

    [Fact]
    public async Task SceneNeuronRefusesReportOnUnknownNodeWithRule()
    {
        var os = fixture.Runtime;
        var id = new NeuronId(new BrainId("facts"), FlameVocabulary.SceneType, "apps/ino/scenes/rules-unit");
        var scene = os.Get<IScene>(id);
        await scene.Set(new([new("orb", "circle", new Transform(), null, [], [], [], ["tap"])], new SceneCamera()), 0);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => scene.Report("missing", "tap", 1, 2, null));
        Assert.Contains("flame.scene-node-exists", ex.Rejected.Rules);
    }
}
