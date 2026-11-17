using CsCheck;
using DigitalBrain.Rules;
using DigitalBrain.Tests.Unit.Rules;
using DigitalBrain.Units;
using DeployMove = DigitalBrain.Rules.Transition<DigitalBrain.Units.DeploymentState>;

namespace DigitalBrain.Tests.Unit.Units;

public sealed class DeploymentProperties
{
    private static readonly string[] Aliases = ["a", "b", "c"];

    private sealed record Step(int Action, int Alias, bool Right, int Hash);

    private static readonly Gen<Step> Steps = Gen.Select(Gen.Int[0, 7], Gen.Int[0, 2], Gen.Bool, Gen.Int[1, 3],
        static (action, alias, right, hash) => new Step(action, alias, right, hash));

    private static StoredBuild Build(int hash)
    {
        var image = new BuildImage(new BuildRef("Me.Test", $"h{hash}"), "TestAssembly", [1], [2]);
        return new StoredBuild(image, new Dictionary<string, string>(), null, Aliases, Aliases.ToDictionary(a => a, a => $"needs-{a}-{hash}", StringComparer.Ordinal));
    }

    private static Outcome<DeploymentState> Apply(DeploymentState state, Step step)
    {
        var alias = Aliases[step.Alias];
        var staged = state.Staged;
        return step.Action switch
        {
            0 => new DeployMove(state.Propose(Build(step.Hash), Aliases.Take(step.Alias + 1))),
            1 => new DeployMove(state.Asked(Aliases.ToDictionary(a => a, a => $"plan-{a}", StringComparer.Ordinal))),
            2 => state.Approve(alias, step.Right && staged is not null ? staged.Build.NeedsHashes[alias] : "stale-needs"),
            3 => state.Deny(alias, step.Right && staged?.Waiting.GetValueOrDefault(alias) is { } plan ? plan : "stale-plan"),
            4 => state.Promote(DateTimeOffset.UnixEpoch.AddMinutes(step.Hash)),
            5 => new DeployMove(state.Supersede()),
            6 => new DeployMove(state.Retire()),
            _ => state.ReadyToPromote ? state.Promote(DateTimeOffset.UnixEpoch) : new DeployMove(state),
        };
    }

    private static readonly Type[] Declared = typeof(DeploymentRefusals).GetNestedTypes();

    [Fact]
    public void ApprovalsOnlyEverClearTheirOwnAliasAndPromotionPublishesTheStagedBuild() =>
        Lifecycle.Holds<DeploymentState, Step>(new DeploymentState(), Steps, static (state, step) => Apply(state, step), static (before, step, outcome) =>
        {
            switch (outcome)
            {
                case DeployMove moved when step.Action == 2:
                    var alias = Aliases[step.Alias];
                    Assert.Equal(before.Staged!.Waiting.Keys.Where(a => a != alias).Order(), moved.Next.Staged!.Waiting.Keys.Order());
                    break;
                case DeployMove moved when step.Action == 3:
                    Assert.Null(moved.Next.Staged);
                    Assert.Equal(before.Live, moved.Next.Live);
                    break;
                case DeployMove moved when (step.Action == 4 || (step.Action == 7 && before.ReadyToPromote)) && before.Staged is { } staged:
                    Assert.Null(moved.Next.Staged);
                    Assert.Equal(staged.Build.Ref, moved.Next.Live!.Ref);
                    break;
                case DeployMove moved:
                    Assert.Equal(moved.Next.Staged is { Waiting.Count: 0 }, moved.Next.ReadyToPromote);
                    break;
                case Refusal refusal:
                    Assert.Contains(refusal.GetType(), Declared);
                    if (step.Action == 2 && step.Right && before.Staged?.Waiting.ContainsKey(Aliases[step.Alias]) == true)
                    {
                        Assert.Fail($"A right approval of a waiting alias was refused: {refusal.Code}.");
                    }
                    break;
                default:
                    Assert.Fail("An outcome with no case.");
                    break;
            }
        }, longest: 40);
}
