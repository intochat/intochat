using DigitalBrain.Rules;

namespace DigitalBrain.Tests.Unit.Rules;

// The smallest aggregate in the house style: a closed union of phases, each phase owning only its legal moves,
// and one data-dependent refusal. The facts in this folder and the property harness use it.
internal sealed record Unlocked(int Opens);

internal sealed record Locked(int Opens, string Code);

internal union Latch(Unlocked, Locked);

internal sealed record WrongCode(string Tried) : Refusal("latch.wrong-code")
{
    public override string Message => "That code does not open the latch.";
}

internal sealed record LockedWith : Signal;

internal sealed record UnlockedWith : Signal;

internal static class LatchMoves
{
    public static Transition<Latch> Close(this Unlocked unlocked, string code) =>
        new(new Locked(unlocked.Opens, code), [new LockedWith()]);

    public static Outcome<Latch> Open(this Locked locked, string code) =>
        code == locked.Code
            ? new Transition<Latch>(new Unlocked(locked.Opens + 1), [new UnlockedWith()])
            : new WrongCode(code);
}
