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
    [Id(2)] public string[] Said { get; set; } = [];
}

// A circle brain's chat `{circle}/chat` (T26-1): one line per Say, numbered here, its speaker the member account that
// said it. Until the circle app split it also serves one brain's copy of a circle's chat: the owner's copy numbers
// each line, with the brain that said it as the speaker, and tells the members; a member's copy pulls the lines after
// the last one it holds, so a missed tell loses nothing. Not reentrant: the owner never awaits a member.
[GrainType(CircleCallGuard.ChatType)]
internal sealed class CircleChatNeuron(
    [PersistentState("circles.chat", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<CircleChatRecord> store,
    ILogger<CircleChatNeuron> logger)
    : Neuron<CircleChatRecord>(store), ICircleChat
{
    private const int Kept = 500;
    private const int KeptRequests = 64;

    private bool IsCore => CircleAddress.TryParse(this.GetPrimaryKeyString()[..^"/chat".Length]) is null;
    private string CircleBrain => this.GetPrimaryKeyString()[..^"/chat".Length];

    private CircleAddress Address => CircleAddress.OfChat(this.GetPrimaryKeyString());
    private ICircle Circle => GrainFactory.GetGrain<ICircle>(IsCore ? ICircle.Key(CircleBrain) : Address.CircleKey);

    // A member of the circle brain, under its own account (T26-8).
    private async Task<string> Member()
    {
        if (!CallerContextStamper.TryGet(out var caller) || caller.BrainId != CircleBrain || !(await Circle.Roster()).Members.Contains(caller.AccountId))
        { throw new UnauthorizedAccessException($"The caller is not a member of circle {CircleBrain}."); }
        return caller.AccountId;
    }

    public async Task Say(string text, string? requestId = null)
    {
        if (IsCore)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            await Publish(await Member(), text, requestId);
            return;
        }
        RequireBrain();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var address = Address;
        if (!(await Circle.Roster()).Members.Contains(address.Brain)) { throw new InvalidOperationException($"{address.Brain} is not in {address.Name}."); }
        if (address.IsOwnerCopy) { await Publish(address.Brain, text, requestId); return; }
        await GrainFactory.GetGrain<ICircleChat>(ICircleChat.Key(address.OwnerKey)).Relay(text, requestId);
        await Sync();
    }

    public async Task<long> Relay(string text, string? requestId = null)
    {
        var speaker = Caller();
        if (!(await Circle.Roster()).Members.Contains(speaker)) { throw new InvalidOperationException($"{speaker} is not in {Address.Name}."); }
        return await Publish(speaker, text, requestId);
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
        if (IsCore)
        {
            await Member();
            return [.. Snapshot.Lines.Where(line => line.Sequence > since)];
        }
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

    // A line said twice under one request id is one line: the owner's copy remembers the last ids per speaker.
    private async Task<long> Publish(string speaker, string text, string? requestId)
    {
        var said = string.IsNullOrWhiteSpace(requestId) ? null : $"{speaker}/{requestId}";
        if (said is not null && Snapshot.Said.Contains(said, StringComparer.Ordinal)) { return Snapshot.Last; }
        if (IsCore)
        {
            var spoken = new CircleLine(speaker, text, Snapshot.Last + 1);
            await Take(spoken, said);
            return spoken.Sequence;
        }
        var address = Address;
        var members = (await Circle.Roster()).Members;
        var line = new CircleLine(speaker, text, Snapshot.Last + 1);
        await Take(line, said);
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

    private async Task Take(CircleLine line, string? said = null)
    {
        if (line.Sequence <= Snapshot.Last) { return; }
        var lines = Snapshot.Lines.Length >= Kept ? Snapshot.Lines[1..] : Snapshot.Lines;
        var name = IsCore ? (await Circle.Roster()).Name : Address.Name;
        await Save(new CircleChatRecord { Lines = [.. lines, line], Last = line.Sequence, Said = said is null ? Snapshot.Said : [.. Snapshot.Said.TakeLast(KeptRequests - 1), said] },
            new CircleSpoke(name, line.Speaker, line.Text, line.Sequence));
        if (!IsCore) { await GrainFactory.GetGrain<ICircles>(ICircles.Key(Address.Brain)).Touch(Address.Name, Address.Owner, "spoke", ""); }
    }
}
