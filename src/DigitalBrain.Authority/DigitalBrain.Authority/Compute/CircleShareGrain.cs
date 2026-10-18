using DigitalBrain.Framework;
using DigitalBrain.Framework.Compute;
using Orleans.Runtime;

namespace DigitalBrain.Authority.Compute;

// Per circle key (the owner's copy): the members who may spend, the share of the owner's budget they may
// spend, and what they have drawn today. A plain grain, not a neuron, so no script can address it.
internal interface ICircleShareGrain : IGrainWithStringKey
{
    // The owner's account when `member` may draw `micros` now; null when that is over the circle's share.
    // A brain outside the circle is refused outright.
    Task<string?> TryDraw(string member, long micros);
    // What ICircleShares.Configure does once the guard has accepted the caller; unreachable outside Platform.
    Task Apply(int percent, string[] members);
}

[GrainType("circle-share")]
internal sealed class CircleShareGrain(
    [PersistentState("circle-share", DigitalBrainNames.PlatformGrainStorage)] IPersistentState<CircleShareState> store,
    TimeProvider clock)
    : Grain, ICircleShares, ICircleShareGrain
{
    private AuthorityNeurons Platform => new(GrainFactory);

    public Task Configure(int percent, string[] members) => Apply(percent, members);

    public async Task Apply(int percent, string[] members)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentNullException.ThrowIfNull(members);
        var brain = BrainKey.BrainOf(this.GetPrimaryKeyString());
        var owner = (await Platform.Brain(brain).Read())?.Owner?.AccountId
            ?? throw new InvalidOperationException($"Brain {brain} has no owner account.");
        store.State.OwnerAccountId = owner;
        store.State.Percent = percent;
        store.State.Members = [.. members];
        await store.WriteStateAsync();
    }

    public async Task<string?> TryDraw(string member, long micros)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(micros);
        if (store.State.OwnerAccountId is not { } owner) { return null; }
        if (!store.State.Members.Contains(member)) { throw new UnauthorizedAccessException($"{member} is not in circle {this.GetPrimaryKeyString()}."); }
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber;
        if (store.State.Day != today) { store.State.Day = today; store.State.DrawnMicros = 0; }
        var balance = await Platform.Ledger(owner).Balance();
        var cap = Math.Max(0, balance) * store.State.Percent / 100 - store.State.DrawnMicros;
        if (micros > cap) { return null; }
        store.State.DrawnMicros += micros;
        await store.WriteStateAsync();
        return owner;
    }
}

// Configure is the circle neuron's alone: the grain the kernel is running the call for must be the circle neuron the key names.
internal sealed class CircleShareGuard : IIncomingGrainCallFilter
{
    private const string CircleNeuronType = CircleGrainType.Name;

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.DeclaringType == typeof(ICircleShares))
        {
            var key = context.TargetId.Key.ToString();
            if (context.SourceId is not { } source || source.Type.ToString() != CircleNeuronType || source.Key.ToString() != key || key!.Contains('@'))
            { throw new UnauthorizedAccessException($"Only the owner's circle {key} states its compute share."); }
        }
        await context.Invoke();
    }
}

[GenerateSerializer, Alias("platform.circle-share-state")]
internal sealed class CircleShareState
{
    [Id(0)] public string? OwnerAccountId { get; set; }
    [Id(1)] public int Percent { get; set; }
    [Id(2)] public long DrawnMicros { get; set; }
    [Id(3)] public string[] Members { get; set; } = [];
    [Id(4)] public int Day { get; set; }
}
