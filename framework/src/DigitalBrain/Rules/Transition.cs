namespace DigitalBrain.Rules;

/// <summary>An accepted move of an aggregate: its next state and the signals it publishes, committed as one journal entry.</summary>
/// <typeparam name="T">The aggregate.</typeparam>
/// <param name="Next">The next state.</param>
/// <param name="Signals">What the move publishes, in order.</param>
public sealed record Transition<T>(T Next, IReadOnlyList<Signal> Signals)
{
    /// <summary>A move with no signal.</summary>
    /// <param name="next">The next state.</param>
    public Transition(T next) : this(next, []) { }
}

/// <summary>What an aggregate method returns when it may say no: a <see cref="Transition{T}"/> or a <see cref="Refusal"/>.</summary>
/// <typeparam name="T">The aggregate.</typeparam>
public union Outcome<T>(Transition<T>, Refusal);
