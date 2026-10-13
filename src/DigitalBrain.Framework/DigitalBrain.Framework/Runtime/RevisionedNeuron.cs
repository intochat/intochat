namespace DigitalBrain.Framework;

public abstract class RevisionedNeuron<TState>(IPersistentState<TState> store) : Neuron<TState>(store)
    where TState : class, IRevisioned<TState>
{
    // False when the operation was already applied: a replay changes nothing and announces nothing.
    protected async Task<bool> Commit(string operationId, long expectedRevision, Func<TState, TState> change, Func<TState, Signal> changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (Snapshot.Operations.Contains(operationId, StringComparer.Ordinal)) { return false; }
        RequireRevision(expectedRevision, Snapshot.Revision);
        var next = change(Snapshot).Advanced(operationId);
        await Save(next, changed(next));
        return true;
    }

    protected static void RequireRevision(long expectedRevision, long currentRevision)
    {
        if (expectedRevision != currentRevision)
        { throw new RevisionConflictException($"Expected revision {expectedRevision}; current revision is {currentRevision}.", currentRevision); }
    }
}
