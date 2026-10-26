using IntoChat.OS.Connections.Platform.Connections;

namespace IntoChat.OS.Authority.Connections;

internal sealed class ConnectionIndexState
{
    public string[] Keys { get; set; } = [];
}

// A plain neuron (06-08), one per brain at "{brain}/main". The keys are bookkeeping no one watches: they save without a signal.
[DigitalBrain.Neuron(IConnectionIndex.TypeName)]
internal sealed class ConnectionIndexNeuron : DigitalBrain.Neuron, IConnectionIndex
{
    private ConnectionIndexState _state = new();

    protected override async Task OnActivate(CancellationToken cancellationToken)
        => _state = await Context.State.Load<ConnectionIndexState>() ?? new();

    public Task<string[]> List() => Task.FromResult<string[]>([.. _state.Keys]);

    public async Task Track(string connectionKey)
    {
        Require(connectionKey);
        if (_state.Keys.Contains(connectionKey, StringComparer.Ordinal)) { return; }
        await Persist(new() { Keys = [.. _state.Keys, connectionKey] });
    }

    public async Task Untrack(string connectionKey)
    {
        Require(connectionKey);
        if (!_state.Keys.Contains(connectionKey, StringComparer.Ordinal)) { return; }
        await Persist(new() { Keys = [.. _state.Keys.Where(key => key != connectionKey)] });
    }

    private async Task Persist(ConnectionIndexState next)
    {
        await Context.State.Save(next, []);
        _state = next;
    }

    private void Require(string connectionKey)
    {
        if (ConnectionKeys.Parse(connectionKey).BrainId != Id.Brain.Value)
        { throw new ArgumentException("The connection belongs to another brain.", nameof(connectionKey)); }
    }
}
