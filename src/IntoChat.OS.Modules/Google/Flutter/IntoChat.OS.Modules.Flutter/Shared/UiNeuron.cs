namespace IntoChat.OS.Modules.Flutter;

// A Flutter control as a plain DigitalBrain neuron (06-04): the members the controls used on the Framework neuron, so the
// migration changes their identity and base only. Snapshot is the current state object, as it was on the Framework neuron;
// FullKey is the os key "{brain}/{key}" the controls name themselves by in state and signals (the shell, scripts and handlers
// match on it).
internal abstract class UiNeuron<TState> : DigitalBrain.Neuron<TState> where TState : class, new()
{
    protected TState Snapshot => State;

    protected string FullKey => $"{Id.Brain.Value}/{Id.Key}";

    protected IServiceProvider ServiceProvider => Context.Services;
}
