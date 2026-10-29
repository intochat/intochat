namespace IntoChat.OS.Connections.Platform.Connections;

// A seam for facts that fault a connection's persistence (06-08): a plain neuron's state store is the runtime's BrainLog, which
// facts cannot wrap as they wrapped the grain store, so ConnectionNeuron reports each write here. No production silo registers it.
public interface IConnectionWriteFaults
{
    // The state the connection activated with (its last committed state).
    void Loaded(string connectionKey, object state);

    // Before a write: next is the state to save, or null for a publish without a save. Throwing fails the write.
    Task BeforeWrite(string connectionKey, object? next, IReadOnlyList<DigitalBrain.Signal> signals);

    // After the write committed. Throwing models a lost acknowledgement.
    Task AfterWrite(string connectionKey, object? next, IReadOnlyList<DigitalBrain.Signal> signals);
}
