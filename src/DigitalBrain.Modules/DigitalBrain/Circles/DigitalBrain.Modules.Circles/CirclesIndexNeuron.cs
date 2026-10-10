using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.index-record")]
internal sealed class CirclesIndexRecord
{
    [Id(0)] public CircleEntry[] Entries { get; set; } = [];
}

[GrainType(CircleCallGuard.IndexType)]
internal sealed class CirclesIndexNeuron(
    [PersistentState("circles.index", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CirclesIndexRecord> store)
    : Neuron<CirclesIndexRecord>(store), ICircles
{
    private static readonly string[] Kinds = ["changed", "invited", "spoke", "left"];
    private static readonly string[] Roles = ["owner", "member", "invited"];

    private string Brain => this.GetPrimaryKeyString()[..^"/circles".Length];

    public Task<CircleEntry[]> Names()
    {
        if (!CallerContextStamper.TryGet(out _) || BrainScope.CurrentId() != Brain)
        { throw new UnauthorizedAccessException($"{this.GetPrimaryKeyString()} belongs to brain {Brain}."); }
        return Task.FromResult(Snapshot.Entries);
    }

    public async Task Touch(string name, string owner, string kind, string role)
    {
        if (!Kinds.Contains(kind)) { throw new ArgumentException($"'{kind}' is not a kind of touch.", nameof(kind)); }
        var signal = new CircleTouched(name, owner, kind);
        var others = Snapshot.Entries.Where(entry => !(entry.Name == name && entry.Owner == owner));
        CircleEntry[] entries = kind == "left" ? [.. others]
            : Roles.Contains(role) ? [.. others, new(name, owner, role)]
            : Snapshot.Entries;
        if (entries.SequenceEqual(Snapshot.Entries)) { await PublishAsync(signal); return; }
        await Save(new CirclesIndexRecord
        {
            Entries = [.. entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.Owner, StringComparer.Ordinal)],
        }, signal);
    }
}
