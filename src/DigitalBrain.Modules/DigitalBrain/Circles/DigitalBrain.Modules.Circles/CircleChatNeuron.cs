using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.chat-record")]
internal sealed class CircleChatRecord
{
    [Id(0)] public CircleLine[] Lines { get; set; } = [];
    [Id(1)] public long Last { get; set; }
}

// One brain's copy of a circle's chat. The owner's copy numbers each line, with the brain that said it as
// the speaker, and tells the members; a member's copy pulls the lines after the last one it holds, so a
// missed tell loses nothing. Not reentrant: the owner never awaits a member.
[GrainType(CircleCallGuard.ChatType)]
internal sealed class CircleChatNeuron(
    [PersistentState("circles.chat", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleChatRecord> store,
    ILogger<CircleChatNeuron> logger)
    : Neuron<CircleChatRecord>(store), ICircleChat
{
    private const int Kept = 500;

    private CircleAddress Address => CircleAddress.OfChat(this.GetPrimaryKeyString());
    private ICircle Circle => GrainFactory.GetGrain<ICircle>(Address.CircleKey);

    public async Task Say(string text)
    {
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var address = Address;
        if (!(await Circle.Roster()).Members.Contains(address.Brain)) { throw new InvalidOperationException($"{address.Brain} is not in {address.Name}."); }
        if (address.IsOwnerCopy) { await Publish(address.Brain, text); return; }
        await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(address.OwnerKey)).Relay(text);
        await Sync();
    }

    public async Task<long> Relay(string text)
    {
        var speaker = Caller();
        if (!(await Circle.Roster()).Members.Contains(speaker)) { throw new InvalidOperationException($"{speaker} is not in {Address.Name}."); }
        return await Publish(speaker, text);
    }

    public async Task<CircleLine[]> Pull(long after)
    {
        var asker = Caller();
        if (!(await Circle.Roster()).Members.Contains(asker)) { throw new InvalidOperationException($"{asker} is not in {Address.Name}."); }
        return [.. Snapshot.Lines.Where(line => line.Sequence > after)];
    }

    public Task Notify() => Sync();

    public async Task<CircleLine[]> Read(long since)
    {
        RequireBrain();
        await Sync();
        return [.. Snapshot.Lines.Where(line => line.Sequence > since)];
    }

    private void RequireBrain()
    {
        if (!CallerContextStamper.TryGet(out _) || BrainScope.CurrentId() != Address.Brain)
        { throw new UnauthorizedAccessException($"{this.GetPrimaryKeyString()} belongs to brain {Address.Brain}."); }
    }

    private static string Caller() => RequestContext.Get(CircleCallGuard.From) as string
        ?? throw new UnauthorizedAccessException("This call does not come from a copy of the circle.");

    private async Task<long> Publish(string speaker, string text)
    {
        var address = Address;
        var members = (await Circle.Roster()).Members;
        var line = new CircleLine(speaker, text, Snapshot.Last + 1);
        await Take(line);
        foreach (var member in members.Where(member => member != address.Brain))
        {
            try { await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(ICircle.Key(member, address.Name, address.Owner))).Notify(); }
            catch (Exception error) { logger.LogWarning(error, "Circle {Circle} could not tell {Brain} of line {Sequence}", address.CircleKey, member, line.Sequence); }
        }
        return line.Sequence;
    }

    // A member's copy takes the owner's lines after its own last one, in order.
    private async Task Sync()
    {
        var address = Address;
        if (address.IsOwnerCopy) { return; }
        CircleLine[] lines;
        try
        {
            if (!(await Circle.Roster()).Members.Contains(address.Brain)) { return; }
            lines = await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(address.OwnerKey)).Pull(Snapshot.Last);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Circle {Circle} could not pull the owner's chat", address.CircleKey);
            return;
        }
        foreach (var line in lines.OrderBy(line => line.Sequence)) { await Take(line); }
    }

    private async Task Take(CircleLine line)
    {
        if (line.Sequence <= Snapshot.Last) { return; }
        var lines = Snapshot.Lines.Length >= Kept ? Snapshot.Lines[1..] : Snapshot.Lines;
        await Save(new CircleChatRecord { Lines = [.. lines, line], Last = line.Sequence },
            new CircleSpoke(Address.Name, line.Speaker, line.Text, line.Sequence));
        await GrainFactory.GetGrain<ICircles>(ICircles.Key(Address.Brain)).Touch(Address.Name, Address.Owner, "spoke", "");
    }
}
