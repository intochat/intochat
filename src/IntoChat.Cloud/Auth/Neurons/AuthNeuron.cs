using DigitalBrain.Neurons;

namespace IntoChat.Cloud.Auth.Neurons;

// The state holder every Auth neuron shares: a mutable record loaded on activation and replaced by SaveQuietly, which writes
// it with no signal (Auth state is internal bookkeeping; nothing subscribes to it). The os full key "{brain}/{key}" is FullKey.
internal abstract class AuthNeuron<TState> : Neuron<TState> where TState : class, new()
{
    private bool _saved;

    protected TState Snapshot => State;

    protected bool Exists => _saved || !IsDefaultState(State);

    protected string FullKey => $"{Id.Brain.Value}/{Id.Key}";

    private static bool IsDefaultState(TState state)
    {
        if (state is AuthCodeState code) { return string.IsNullOrEmpty(code.Id); }
        if (state is AuthCredentialState cred) { return string.IsNullOrEmpty(cred.PasswordHash); }
        return false;
    }

    protected async Task SaveQuietly(TState next)
    {
        await Store(next).ConfigureAwait(true);
        _saved = true;
    }
}
