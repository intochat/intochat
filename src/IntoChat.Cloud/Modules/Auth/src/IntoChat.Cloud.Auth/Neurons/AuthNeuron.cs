using DigitalBrain;

namespace IntoChat.Cloud.Auth.Neurons;

// The state holder every Auth neuron shares: a mutable record loaded on activation and replaced by SaveQuietly, which writes
// it with no signal (Auth state is internal bookkeeping; nothing subscribes to it). The os full key "{brain}/{key}" is FullKey.
internal abstract class AuthNeuron<TState> : Neuron where TState : class, new()
{
    private TState? state;

    protected TState Snapshot => state ?? new TState();

    protected bool Exists => state is not null;

    protected string FullKey => $"{Id.Brain.Value}/{Id.Key}";

    protected IServiceProvider ServiceProvider => Context.Services;

    protected override async Task OnActivate(CancellationToken cancellationToken) => state = await Context.State.Load<TState>();

    protected async Task SaveQuietly(TState next)
    {
        await Context.State.Save(next, []);
        state = next;
    }
}
