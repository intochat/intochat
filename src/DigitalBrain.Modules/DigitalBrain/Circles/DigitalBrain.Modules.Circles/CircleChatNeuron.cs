using DigitalBrain.Circles.Signals;
using DigitalBrain.Framework;

namespace DigitalBrain.Circles;

[GenerateSerializer, Alias("circles.chat-record")]
internal sealed class CircleChatRecord
{
    [Id(0)] public CircleLine[] Lines { get; set; } = [];
    [Id(1)] public long Last { get; set; }
    [Id(2)] public string[] Said { get; set; } = [];
}

// A circle brain's chat `{circle}/chat` (T26-1): one line per Say, numbered here, its speaker the member account that
// said it. A line said twice under one request id is one line.
[GrainType("circles.chat")]
internal sealed class CircleChatNeuron(
    [PersistentState("circles.chat", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleChatRecord> store)
    : Neuron<CircleChatRecord>(store), ICircleChat
{
    private const int Kept = 500;
    private const int KeptRequests = 64;

    private string CircleBrain => this.GetPrimaryKeyString()[..^"/chat".Length];

    private ICircle Circle => GrainFactory.GetGrain<ICircle>(ICircle.Key(CircleBrain));

    public async Task Say(string text, string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var (speaker, circle) = await Member();
        var said = string.IsNullOrWhiteSpace(requestId) ? null : $"{speaker}/{requestId}";
        if (said is not null && Snapshot.Said.Contains(said, StringComparer.Ordinal)) { return; }
        var line = new CircleLine(speaker, text, Snapshot.Last + 1);
        var lines = Snapshot.Lines.Length >= Kept ? Snapshot.Lines[1..] : Snapshot.Lines;
        await Save(new CircleChatRecord { Lines = [.. lines, line], Last = line.Sequence, Said = said is null ? Snapshot.Said : [.. Snapshot.Said.TakeLast(KeptRequests - 1), said] },
            new CircleSpoke(circle.Name, line.Speaker, line.Text, line.Sequence));
    }

    public async Task<CircleLine[]> Read(long since)
    {
        await Member();
        return [.. Snapshot.Lines.Where(line => line.Sequence > since)];
    }

    // A member of the circle brain, under its own account (T26-8).
    private async Task<(string Account, CircleState Circle)> Member()
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.BrainId != CircleBrain)
        { throw new UnauthorizedAccessException($"The caller is not in circle {CircleBrain}."); }
        var circle = await Circle.State();
        if (!circle.Members.Contains(caller.AccountId)) { throw new UnauthorizedAccessException($"The caller is not a member of circle {CircleBrain}."); }
        return (caller.AccountId, circle);
    }
}
