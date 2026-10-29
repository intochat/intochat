using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Compute;
using IntoChat.OS.Framework.Compute;
using Orleans.Runtime;

namespace IntoChat.OS.Authority.Compute;

// Per circle key (the owner's copy): the members who may spend, the share of the owner's budget they may
// spend, and what they have drawn today. A plain grain, not a neuron, so no script can address it.
[Alias("compute.circle-share")]
internal interface ICircleShareGrain : IGrainWithStringKey
{
    // The owner's account when `member` may draw `micros` now; null when that is over the circle's share.
    // A brain outside the circle is refused outright.
    Task<string?> TryDraw(string member, long micros);
    // What ICircleShares.Configure does once the guard has accepted the caller; unreachable outside Platform.
    Task Apply(int percent, string[] members);
    Task<string?> ReadOwner();
    Task<string?> TryReserve(ReservationKey key, string member, long maximumMicros);
    Task Settle(ReservationKey key, long actualMicros);
    Task Release(ReservationKey key);
    Task Acknowledge(ReservationKey key);
}

[GrainType("circle-share")]
internal sealed class CircleShareGrain(
    [PersistentState("circle-share", IntoChatOSNames.PlatformGrainStorage)] IPersistentState<CircleShareState> store,
    TimeProvider clock)
    : Grain, ICircleShares, ICircleShareGrain
{
    private AuthorityNeurons Platform => ServiceProvider.GetRequiredService<AuthorityNeurons>();
    public Task<string?> ReadOwner() => Task.FromResult(store.State.OwnerAccountId);

    public async Task<string?> TryReserve(ReservationKey key, string member, long maximumMicros)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMicros);
        if (store.State.Reservations.FirstOrDefault(value => value.Key == key) is { } previous)
        {
            if (previous.MaximumMicros != maximumMicros || previous.Member != member) { throw new InvalidOperationException("Conflicting circle reservation."); }
            await store.WriteStateAsync();
            return previous.Payer;
        }
        if (store.State.OwnerAccountId is not { } owner) { return null; }
        if (!store.State.Members.Contains(member)) { throw new UnauthorizedAccessException("Account is not a circle member."); }
        ResetDay();
        var balance = await Platform.Ledger(owner).Balance();
        var held = store.State.Reservations.Where(value => value.SettledMicros is null).Sum(value => value.MaximumMicros);
        var cap = checked(Math.Max(0, balance) * store.State.Percent / 100 - store.State.DrawnMicros - held);
        if (maximumMicros > cap) { return null; }
        store.State.Reservations = [.. store.State.Reservations, new(key, maximumMicros, Payer: owner, Member: member)];
        await store.WriteStateAsync();
        return owner;
    }

    public async Task Settle(ReservationKey key, long actualMicros)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualMicros);
        var index = Array.FindIndex(store.State.Reservations, value => value.Key == key);
        if (index < 0) { throw new InvalidOperationException("Missing circle reservation."); }
        var reservation = store.State.Reservations[index];
        if (actualMicros > reservation.MaximumMicros || reservation.SettledMicros is { } settled && settled != actualMicros)
        { throw new InvalidOperationException("Conflicting circle settlement."); }
        if (reservation.SettledMicros is null)
        {
            ResetDay();
            store.State.DrawnMicros = checked(store.State.DrawnMicros + actualMicros);
            store.State.Reservations[index] = reservation with { SettledMicros = actualMicros };
        }
        await store.WriteStateAsync();
    }

    public Task Release(ReservationKey key) => Settle(key, 0);
    public async Task Acknowledge(ReservationKey key)
    {
        if (store.State.Reservations.Any(value => value.Key == key && value.SettledMicros is null)) { throw new InvalidOperationException("Cannot forget an unsettled circle hold."); }
        store.State.Reservations = [.. store.State.Reservations.Where(value => value.Key != key)];
        await store.WriteStateAsync();
    }

    private void ResetDay()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber;
        if (store.State.Day != today) { store.State.Day = today; store.State.DrawnMicros = 0; }
    }

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
        var cap = checked(Math.Max(0, balance) * store.State.Percent / 100 - store.State.DrawnMicros
            - store.State.Reservations.Where(value => value.SettledMicros is null).Sum(value => value.MaximumMicros));
        if (micros > cap) { return null; }
        store.State.DrawnMicros += micros;
        await store.WriteStateAsync();
        return owner;
    }
}

// Configure is the circle neuron's alone: the neuron the kernel is running the call for must be the circle neuron the key names.
// The circle is a plain neuron (06-08): its call leaves from its runtime host grain, keyed by its neuron id "brain/type/key", read
// from the ids Orleans sets on the message (never from the call's body).
internal sealed class CircleShareGuard : IIncomingGrainCallFilter
{
    private const string CircleNeuronType = CircleGrainType.Name;

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod.DeclaringType == typeof(ICircleShares))
        {
            var key = context.TargetId.Key.ToString();
            if (SourceOf(context.SourceId) is not { } source || source.Type != CircleNeuronType || source.Key != key || key!.Contains('@'))
            { throw new UnauthorizedAccessException($"Only the owner's circle {key} states its compute share."); }
        }
        await context.Invoke();
    }

    private static (string Type, string Key)? SourceOf(GrainId? source)
    {
        if (source is not { } id) { return null; }
        var type = id.Type.ToString()!;
        var key = id.Key.ToString()!;
        if (type.StartsWith("neuron.host", StringComparison.Ordinal))
        {
            return DigitalBrain.NeuronId.TryParse(key, out var neuron) ? (neuron.Type, $"{neuron.Brain.Value}/{neuron.Key}") : null;
        }
        return (type, key);
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
    [Id(5)] public FundingReservation[] Reservations { get; set; } = [];
}
