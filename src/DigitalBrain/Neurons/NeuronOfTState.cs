namespace DigitalBrain;

/// <summary>A neuron with persisted state. State is saved only when <see cref="Save"/> is called.</summary>
public abstract class Neuron<TState> : Neuron where TState : new()
{
    /// <summary>The current state; loaded by the runtime before activation.</summary>
    protected TState State { get; private set; } = new();

    /// <summary>Loads the persisted state, or keeps a new one when none exists.</summary>
    protected async Task Load()
    {
        State = await Context.State.Load<TState>().ConfigureAwait(false) ?? new TState();
    }

    /// <summary>Persists the current state.</summary>
    protected Task Save() => Context.State.Save(State);
}
