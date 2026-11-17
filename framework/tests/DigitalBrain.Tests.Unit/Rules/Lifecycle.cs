using CsCheck;
using DigitalBrain.Rules;

namespace DigitalBrain.Tests.Unit.Rules;

// The property harness for an aggregate: from a start, apply random sequences of steps. A step returns null when its method does not
// exist on the current phase (the type system already refuses it), a refusal, or a transition. After every step the invariant holds
// between the state before and the outcome, and a refusal leaves the state as it was.
internal static class Lifecycle
{
    public static void Holds<TState, TStep>(
        TState start,
        Gen<TStep> steps,
        Func<TState, TStep, Outcome<TState>?> apply,
        Action<TState, TStep, Outcome<TState>> invariant,
        int longest = 40) =>
        steps.List[0, longest].Sample(sequence =>
        {
            var state = start;
            foreach (var step in sequence)
            {
                if (apply(state, step) is not { } outcome) { continue; }
                invariant(state, step, outcome);
                if (outcome.Value is DigitalBrain.Rules.Transition<TState> moved) { state = moved.Next; }
            }
        });
}
