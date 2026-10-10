using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.chat-record")]
internal sealed class CircleChatRecord
{
    [Id(0)] public CircleLine[] Lines { get; set; } = [];
    [Id(1)] public long Last { get; set; }
}

// One brain's copy of a circle's chat. The owner's copy numbers each line and hands it to every member's
// copy; a member's Say goes to the owner and is then recorded here. Reentrant for the same reason as the
// circle: a member's Say is in flight while the owner relays someone else's line to it.
[Orleans.Concurrency.Reentrant]
[GrainType("circles.chat")]
internal sealed class CircleChatNeuron(
    [PersistentState("circles.chat", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleChatRecord> store,
    ILogger<CircleChatNeuron> logger)
    : Neuron<CircleChatRecord>(store), ICircleChat
{
    private const int Kept = 500;
    private const string Suffix = "/chat";

    private string CircleKey => this.GetPrimaryKeyString()[..^Suffix.Length];
    private string Brain => BrainKey.BrainOf(CircleKey);
    private string Name => CircleKey[(Brain.Length + "/circles/".Length)..];
    private ICircle Circle => GrainFactory.GetGrain<ICircle>(CircleKey);

    public async Task Say(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var circle = await Circle.State();
        var speaker = CallerContextStamper.TryGet(out var caller) ? caller.PrincipalId : Brain;
        if (!circle.Members.Contains(Brain)) { throw new InvalidOperationException($"{Brain} is not in {Name}."); }
        if (circle.Owner == Brain) { await Relay(speaker, text, Brain); return; }
        var sequence = await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(ICircle.Key(circle.Owner, Name))).Relay(speaker, text, Brain);
        await Hear(new CircleLine(speaker, text, sequence));
    }

    public async Task<long> Relay(string speaker, string text, string fromBrain)
    {
        var circle = await Circle.State();
        if (circle.Owner != Brain) { throw new InvalidOperationException($"Only the owner's chat of {Name} numbers its lines."); }
        if (!circle.Members.Contains(fromBrain)) { throw new InvalidOperationException($"{fromBrain} is not in {Name}."); }
        var line = new CircleLine(speaker, text, Snapshot.Last + 1);
        await Take(line);
        foreach (var member in circle.Members.Where(member => member != Brain && member != fromBrain))
        {
            try { await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(ICircle.Key(member, Name))).Hear(line); }
            catch (Exception error) { logger.LogWarning(error, "Circle {Circle} could not tell {Brain} of line {Sequence}", CircleKey, member, line.Sequence); }
        }
        return line.Sequence;
    }

    public Task Hear(CircleLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return Take(line);
    }

    public Task<CircleLine[]> Read(long since) => Task.FromResult(Snapshot.Lines.Where(line => line.Sequence > since).ToArray());

    // Delivery is at-least-once: a line already taken is not taken twice.
    private Task Take(CircleLine line)
    {
        if (line.Sequence <= Snapshot.Last) { return Task.CompletedTask; }
        var lines = Snapshot.Lines.Length >= Kept ? Snapshot.Lines[1..] : Snapshot.Lines;
        return Save(new CircleChatRecord { Lines = [.. lines, line], Last = line.Sequence },
            new CircleSpoke(Name, line.Speaker, line.Text, line.Sequence));
    }
}
