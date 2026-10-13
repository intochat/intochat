using System.Text.Json.Nodes;
using DigitalBrain.Framework;

namespace DigitalBrain.Flutter.Workspace;

[GrainType("intochat.shell-state")]
internal sealed class ShellStateNeuron([PersistentState("head", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<ShellHeadState> state) : Neuron, IShellState
{
    // The shell is the person's: its key is the digest of account and principal inside the brain
    // their session is in, so the only stamped caller admitted is the one whose key this is.
    private void RequireOwner()
    {
        if (CallerContextStamper.TryGet(out var caller) && caller.Kind != CallerKind.Platform
            && ShellPersistenceEndpoints.Scope(caller.BrainId, caller.AccountId, caller.PrincipalId) != this.GetPrimaryKeyString())
        { throw new UnauthorizedAccessException("This workspace belongs to another person."); }
    }

    private IShellPart Part(string hash) => GrainFactory.GetGrain<IShellPart>(this.GetPrimaryKeyString() + "/" + hash);
    public Task<ShellRead> Read() { RequireOwner(); return Load(); }

    private async Task<ShellRead> Load() => new(state.State.Revision, state.State.Root is { } root
        ? (await ShellSnapshotParts.Read(root, hash => Part(hash).Read())).ToJsonString() : null);

    public async Task<ShellRead> Save(long expectedRevision, string operationId, string json)
    {
        RequireOwner();
        if (!Guid.TryParse(operationId, out _)) { throw new ArgumentException("Use a unique operation ID."); }
        var digest = ShellSnapshotParts.Digest(json);
        if (state.State.LastOperation == operationId)
        {
            if (state.State.LastDigest != digest) { throw new InvalidOperationException("Operation ID was reused with different data."); }
            return await Load();
        }
        if (state.State.Revision != expectedRevision) { throw new RevisionConflictException("Workspace changed on another client. Reload before saving.", state.State.Revision); }
        var snapshot = JsonNode.Parse(json) ?? throw new ArgumentException("Missing snapshot.");
        ShellSnapshotParts.Validate(snapshot);
        var root = await ShellSnapshotParts.Write(snapshot, (hash, value) => Part(hash).Put(value));
        var old = state.State;
        state.State = new()
        {
            Root = root,
            Revision = old.Revision + 1,
            LastOperation = operationId,
            LastDigest = digest
        };
        try { await state.WriteStateAsync(); } catch { state.State = old; throw; }
        return await Load();
    }
}
