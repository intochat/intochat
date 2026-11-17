using DigitalBrain.Rules;

namespace DigitalBrain.Tests.Unit.Rules;

public sealed class RefusalFacts
{
    [Fact]
    public void ARefusalIsPublishedWithItsCodeAndMessage()
    {
        var rejected = new WrongCode("0000").ToRejected("latch.open");

        Assert.Equal("latch.open", rejected.Operation);
        Assert.Equal(["latch.wrong-code"], rejected.Rules);
        Assert.Equal(["That code does not open the latch."], rejected.Messages);
    }

    [Fact]
    public void TheRightCodeIsATransition()
    {
        var outcome = new Locked(2, "1234").Open("1234");

        var transition = Assert.IsType<Transition<Latch>>(outcome.Value);
        Assert.Equal(new Unlocked(3), transition.Next.Value);
        Assert.IsType<UnlockedWith>(Assert.Single(transition.Signals));
    }

    [Fact]
    public void TheWrongCodeIsARefusalAndNothingMoves()
    {
        var outcome = new Locked(2, "1234").Open("0000");

        Assert.Equal(new WrongCode("0000"), outcome.Value);
    }

    [Fact]
    public void AnOutcomeSwitchNamesEveryCase()
    {
        static string Describe(Outcome<Latch> outcome) => outcome switch
        {
            Transition<Latch> moved => $"moved, {moved.Signals.Count} signal",
            Refusal refused => refused.Code,
        };

        Assert.Equal("moved, 1 signal", Describe(new Locked(0, "1").Open("1")));
        Assert.Equal("latch.wrong-code", Describe(new Locked(0, "1").Open("2")));
    }

    [Fact]
    public void ATransitionWithoutSignalsHasAnEmptyList() => Assert.Empty(new Transition<Latch>(new Unlocked(0)).Signals);
}
