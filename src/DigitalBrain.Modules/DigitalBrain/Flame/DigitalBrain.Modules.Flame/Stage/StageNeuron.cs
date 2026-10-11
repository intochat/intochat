using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Flame.Stage;
using DigitalBrain.Flame.Stage.Signals;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Flame;

// The stage is the person's scene stack: Home is its floor, Escape pops, and only the session whose
// scope digest is the key may read or move it (the same rule as the shell's workspace neuron).
[GrainType(FlameVocabulary.StageType)]
internal sealed class StageNeuron([PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<StageState> store)
    : Neuron<StageState>(store), IStage
{
    private const string OwnerAlias = "/stage/owner";

    // A synapse cannot compute a person's digest, so the brain owner's stage is also addressable as
    // {brain}/stage/owner. The owner account is the one IBrain.Create recorded for that brain.
    private async Task RequireOwner()
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.Kind == CallerKind.Platform) { return; }
        var key = this.GetPrimaryKeyString();
        if (ScopeOf(caller) == key) { return; }
        if (key == caller.BrainId + OwnerAlias && await OwnsBrain(caller)) { return; }
        throw new UnauthorizedAccessException("This stage belongs to another person.");
    }

    private async Task<bool> OwnsBrain(CallerContext caller)
    {
        try { return (await GrainFactory.GetGrain<IBrain>(caller.BrainId).Read()).Owner.AccountId == caller.AccountId; }
        catch (InvalidOperationException) { return false; }
    }

    // Mirrors ShellPersistenceEndpoints.Scope exactly, with "/stage/" for "/shell/":
    // {brainId}/stage/{lowercase hex SHA-256 of UTF-8 (accountId + "\0" + principalId)}, all 64 characters.
    internal static string ScopeOf(CallerContext caller)
        => caller.BrainId + "/stage/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caller.AccountId + "\0" + caller.PrincipalId)));

    private StageState Next(string home, StageEntry[] stack)
        => new() { Name = this.GetPrimaryKeyString(), Revision = Snapshot.Revision + 1, Home = home, Stack = stack };

    public async Task SetHome(string sceneKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneKey);
        await RequireOwner();
        var next = Next(sceneKey, [new StageEntry(sceneKey, null)]);
        await Save(next, new StageChanged(next.Name, next.Revision, sceneKey));
    }

    public async Task Go(string sceneKey, string? dockSurfaceKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneKey);
        await RequireOwner();
        RequireHome();
        var next = Next(Snapshot.Home, [.. Snapshot.Stack, new StageEntry(sceneKey, dockSurfaceKey)]);
        await Save(next, new StageChanged(next.Name, next.Revision, sceneKey));
    }

    public async Task Back()
    {
        await RequireOwner();
        if (Snapshot.Stack.Length <= 1) { return; }
        var next = Next(Snapshot.Home, Snapshot.Stack[..^1]);
        await Save(next, new StageChanged(next.Name, next.Revision, next.Stack[^1].SceneKey));
    }

    public async Task Home()
    {
        await RequireOwner();
        RequireHome();
        var home = Snapshot.Home;
        if (Snapshot.Stack is [{ DockSurfaceKey: null } only] && only.SceneKey == home) { return; }
        var next = Next(home, [new StageEntry(home, null)]);
        await Save(next, new StageChanged(next.Name, next.Revision, home));
    }

    private void RequireHome()
    {
        if (Snapshot.Home.Length == 0) { throw new InvalidOperationException("Set a home scene before navigating."); }
    }

    [ReadOnly]
    public async Task<StageState> Read()
    {
        await RequireOwner();
        Snapshot.Name = this.GetPrimaryKeyString();
        return Snapshot;
    }
}
