using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.factory-record")]
internal sealed class CircleFactoryRecord
{
    [Id(0)] public CircleEntry[] Created { get; set; } = [];
}

// R126: `{brain}/circles` in a member's own brain. Create records a brain of kind Circle for the caller's account and
// gives it birth as its creator (the same birth BrainBirths gives: IBrain.Create stamped as the owner, inside the new
// brain); the circle brain's creation handler (CircleBirth) founds its core and installs its room.
[GrainType("circles.factory")]
internal sealed class CircleFactoryNeuron(
    [PersistentState("circles.factory", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleFactoryRecord> store,
    IIdentityClient identity,
    CircleBirth birth)
    : Neuron<CircleFactoryRecord>(store), ICircleFactory
{
    private string Brain => this.GetPrimaryKeyString()[..^"/circles".Length];

    public async Task<string> Create(string name, string? requestId = null)
    {
        if (!ICircle.IsValidName(name)) { throw new ArgumentException("A circle name is 1 to 40 of a-z, 0-9 and '-'.", nameof(name)); }
        var caller = Own();
        var operation = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : $"circle|{requestId}";
        var provision = await identity.CreateBrain(caller.AccountId, caller.PrincipalId, operation, name, BrainKind.Circle);
        var circle = provision.Member.BrainId;
        CallerContextStamper.Stamp(caller with { BrainId = circle });
        try
        {
            // A retried Create completes a birth that failed before or inside the brain's creation: IBrain.Create is a
            // no-op once the brain exists, and a creation handler's failure is only logged there, so the birth runs again.
            await GrainFactory.GetGrain<IBrain>(circle).Create(new Owner(caller.AccountId));
            if (!provision.NewBrain) { await birth.Born(circle, CancellationToken.None); }
        }
        finally { CallerContextStamper.Stamp(caller); }
        if (!Snapshot.Created.Any(created => created.BrainId == circle))
        { await SaveQuietly(new CircleFactoryRecord { Created = [.. Snapshot.Created, new(circle, name)] }); }
        return circle;
    }

    public async Task<CircleEntry[]> Circles()
    {
        var caller = Own();
        return [.. (await identity.BrainsOf(caller.PrincipalId)).Where(entry => entry.Kind == BrainKind.Circle)
            .Select(entry => Snapshot.Created.FirstOrDefault(created => created.BrainId == entry.Id) ?? new CircleEntry(entry.Id, entry.Id))];
    }

    // The member acts in its own brain.
    private CallerContext Own()
    {
        if (!CallerContextStamper.TryGet(out var caller) || BrainScope.Create(caller.AccountId, caller.BrainId).Id != Brain)
        { throw new UnauthorizedAccessException($"{this.GetPrimaryKeyString()} belongs to brain {Brain}."); }
        return caller;
    }
}
