using CsCheck;
using DigitalBrain.Rules;

namespace DigitalBrain.Tests.Unit.Rules;

public sealed class LatchProperties
{
    private sealed record Step(bool Close, string Code);

    private static readonly Gen<Step> Steps =
        Gen.Select(Gen.Bool, Gen.Int[1, 3], static (close, code) => new Step(close, code.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static Outcome<Latch>? Apply(Latch latch, Step step) => (latch, step.Close) switch
    {
        (Unlocked unlocked, true) => (Outcome<Latch>)unlocked.Close(step.Code),
        (Locked locked, false) => (Outcome<Latch>?)locked.Open(step.Code),
        _ => null,
    };

    [Fact]
    public void OpensCountOnlyRightCodesAndARefusalNamesTheCodeTried() =>
        Lifecycle.Holds<Latch, Step>(new Unlocked(0), Steps, Apply, static (before, step, outcome) =>
        {
            switch (outcome)
            {
                case DigitalBrain.Rules.Transition<Latch> { Next.Value: Unlocked after }:
                    var locked = Assert.IsType<Locked>(before.Value);
                    Assert.Equal(locked.Code, step.Code);
                    Assert.Equal(locked.Opens + 1, after.Opens);
                    break;
                case DigitalBrain.Rules.Transition<Latch> { Next.Value: Locked after }:
                    Assert.Equal(Assert.IsType<Unlocked>(before.Value).Opens, after.Opens);
                    Assert.Equal(step.Code, after.Code);
                    break;
                case Refusal refusal:
                    Assert.Equal(new WrongCode(step.Code), refusal);
                    Assert.NotEqual(Assert.IsType<Locked>(before.Value).Code, step.Code);
                    break;
                default:
                    Assert.Fail($"Unexpected outcome {outcome.Value}.");
                    break;
            }
        });
}
