using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Connections;

namespace DigitalBrain.Platform.Connections;

[GenerateSerializer, Alias("connection.index-state")]
internal sealed class ConnectionIndexState
{
    [Id(0)] public string[] Keys { get; set; } = [];
}

[GrainType("connection.index")]
internal sealed class ConnectionIndexNeuron : Grain, IConnectionIndex
{
    private readonly IPersistentState<ConnectionIndexState> _store;

    public ConnectionIndexNeuron(
        [PersistentState("connections", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<ConnectionIndexState> store)
        => _store = store;

    public Task<string[]> List() => Task.FromResult<string[]>([.. _store.State.Keys]);

    public async Task Track(string connectionKey)
    {
        Require(connectionKey);
        if (_store.State.Keys.Contains(connectionKey, StringComparer.Ordinal)) { return; }
        _store.State.Keys = [.. _store.State.Keys, connectionKey];
        await _store.WriteStateAsync();
    }

    public async Task Untrack(string connectionKey)
    {
        Require(connectionKey);
        if (!_store.State.Keys.Contains(connectionKey, StringComparer.Ordinal)) { return; }
        _store.State.Keys = [.. _store.State.Keys.Where(key => key != connectionKey)];
        await _store.WriteStateAsync();
    }

    private void Require(string connectionKey)
    {
        if (ConnectionKeys.Parse(connectionKey).BrainId != this.GetPrimaryKeyString())
        { throw new ArgumentException("The connection belongs to another brain.", nameof(connectionKey)); }
    }
}
