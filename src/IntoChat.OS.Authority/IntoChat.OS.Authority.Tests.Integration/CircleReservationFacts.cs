using IntoChat.OS.Authority.Compute;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

[Trait("host", "own")]
public sealed class CircleReservationFacts
{
    [Fact]
    public async Task HoldsSurviveMidnightAndMembershipChangesUntilThePinnedOwnerIsSettled()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new CircleClock();
        await using var brain = await TestBrain.Create().ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)).StartAsync(ct);
        using var owner = await brain.PersonAsync("circle-owner", ct);
        using var member = await brain.PersonAsync("member", ct);
        var circle = brain.Platform.CircleShare(owner.Key("circles/c"));
        await brain.Platform.Ledger(owner.AccountId).Credit("grant", 1_000, "test");
        await circle.Apply(20, [member.AccountId]);
        var key = new ReservationKey("trace", "held");
        Assert.Equal(owner.AccountId, await circle.TryReserve(key, member.AccountId, 150));
        clock.Now = clock.Now.AddDays(1);
        Assert.Null(await circle.TryReserve(new("trace", "blocked"), member.AccountId, 51));
        await circle.Apply(20, []);
        await brain.DeactivateAsync(circle, ct);
        Assert.Equal(owner.AccountId, await circle.TryReserve(key, member.AccountId, 150));
        await circle.Settle(key, 50);
        await circle.Settle(key, 50);
        await circle.Acknowledge(key);
        await circle.Apply(20, [member.AccountId]);
        Assert.Equal(owner.AccountId, await circle.TryReserve(new("trace", "next"), member.AccountId, 150));
        Assert.Null(await circle.TryReserve(new("trace", "over"), member.AccountId, 1));
    }

    private sealed class CircleClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow.AddDays(-2);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
