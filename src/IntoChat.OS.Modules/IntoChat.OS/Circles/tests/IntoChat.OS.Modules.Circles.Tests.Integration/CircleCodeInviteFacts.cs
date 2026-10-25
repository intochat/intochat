using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Storage;
using static IntoChat.OS.Modules.Circles.Tests.CircleBrainRooms;

namespace IntoChat.OS.Modules.Circles.Tests;

// D-03/D-10: owners and admins mint single-use, expiring codes; a machine redeems once, binding its id and key; an
// account binds at mint or by claim; only the code's HMAC is stored.
public sealed class CircleCodeInviteFacts(CirclesHost host)
{
    private sealed record Minted(string Code, DateTimeOffset ExpiresAt);

    [Fact]
    public async Task OnlyOwnersAndAdminsMintACodeOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var (alice, bob, carol, outsider) = (await host.PersonAsync("alice", ct), await host.PersonAsync("bob", ct),
            await host.PersonAsync("carol", ct), await host.PersonAsync("outsider", ct));
        var circle = await Create(brain, alice);
        await Join(brain, circle, alice, bob);
        await Join(brain, circle, alice, carol);
        await alice.In(circle, () => Circle(brain, circle).SetAdmin(carol.AccountId, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => carol.In(circle, () => Circle(brain, circle).SetAdmin(bob.AccountId, true)));
        var route = $"/circle-invitations/{circle}/codes";

        using var anonymous = await brain.HttpClient.PostAsJsonAsync(route, new { }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var stranger = await outsider.Http.PostAsJsonAsync(route, new { }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        using var member = await bob.Http.PostAsJsonAsync(route, new { }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        var before = DateTimeOffset.UtcNow;
        using var owner = await alice.Http.PostAsJsonAsync(route, new { validForMinutes = 1 }, ct);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Contains("no-store", owner.Headers.CacheControl?.ToString() ?? "", StringComparison.Ordinal);
        var short_ = (await owner.Content.ReadFromJsonAsync<Minted>(ct))!;
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", short_.Code);
        Assert.InRange(short_.ExpiresAt - before, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));

        using var admin = await carol.Http.PostAsJsonAsync(route, new { validForMinutes = 100_000 }, ct);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var long_ = (await admin.Content.ReadFromJsonAsync<Minted>(ct))!;
        Assert.InRange(long_.ExpiresAt - before, TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        Assert.NotEqual(short_.Code, long_.Code);
    }

    [Trait("host", "own")]
    [Fact(Timeout = 120_000)]
    public async Task AMachineRedeemsACodeOnceAndAnAccountBindsAtMintOrByClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var brain = await Start(ct, configure => configure.ConfigureServices(services =>
            services.AddKeyedSingleton<TimeProvider>(CircleInvitesNeuron.ClockKey, clock)));
        var (alice, bob, outsider) = (await brain.PersonAsync("alice", ct), await brain.PersonAsync("bob", ct), await brain.PersonAsync("outsider", ct));
        var circle = await Create(brain, alice, "crew");
        await Join(brain, circle, alice, bob);
        var invites = brain.Get<ICircleInvites>(ICircleInvites.Key(circle));
        var redeemer = brain.Services.GetRequiredService<ICircleInviteRedeemer>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(circle, () => invites.Mint(new MintInvite(TimeSpan.FromHours(1)))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => outsider.In(circle, () => invites.Mint(new MintInvite(TimeSpan.FromHours(1)))));

        // Unbound: the machine is pending an account until a signed-in member claims it.
        var code = await alice.In(circle, () => invites.Mint(new MintInvite(TimeSpan.FromHours(1))));
        var accepted = await redeemer.Redeem(code.Code.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal), "m1", "thumb-1", "desk");
        Assert.Equal(new RedeemOutcome(RedeemResult.Accepted, circle, "crew", "member", null, PendingAccount: true), accepted);
        Assert.Equal(RedeemResult.Rejected, (await redeemer.Redeem(code.Code, "m2", "thumb-2", "laptop")).Result);
        Assert.Equal(RedeemResult.Rejected, (await redeemer.Redeem(code.Code, "m1", "thumb-other", "desk")).Result);
        Assert.Equal(accepted with { Result = RedeemResult.Repeat }, await redeemer.Redeem(code.Code, "m1", "thumb-1", "desk"));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(RedeemResult.Rejected, (await redeemer.Redeem(code.Code, "m1", "thumb-1", "desk")).Result);

        Assert.Equal(RedeemOutcome.Rejected, await redeemer.Redeem(InviteCode.Generate(), "m3", "thumb-3", "x"));
        Assert.Equal(RedeemOutcome.Rejected, await redeemer.Redeem("not-a-code", "m3", "thumb-3", "x"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => outsider.In(circle, () => invites.ClaimMachine("m1")));
        var claimed = await bob.In(circle, () => invites.ClaimMachine("m1"));
        Assert.Equal(bob.AccountId, claimed.AccountId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.In(circle, () => invites.ClaimMachine("m1")));

        // Bound at mint: the minting account rides with the code.
        var bound = await alice.In(circle, () => invites.Mint(new MintInvite(TimeSpan.FromHours(1), "admin", BindCaller: true)));
        var boundOutcome = await redeemer.Redeem(bound.Code, "m4", "thumb-4", "server");
        Assert.Equal(new RedeemOutcome(RedeemResult.Accepted, circle, "crew", "admin", alice.AccountId, PendingAccount: false), boundOutcome);

        // Expiry.
        var stale = await alice.In(circle, () => invites.Mint(new MintInvite(TimeSpan.Zero)));
        Assert.Equal(clock.GetUtcNow() + InviteCode.MinValidity, stale.ExpiresAt);
        clock.Advance(InviteCode.MinValidity + TimeSpan.FromSeconds(1));
        Assert.Equal(RedeemOutcome.Expired, await redeemer.Redeem(stale.Code, "m5", "thumb-5", "late"));

        var machines = await bob.In(circle, () => invites.ListMachines());
        Assert.Equal(["m1", "m4"], machines.Select(machine => machine.MachineId).Order().ToArray());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => bob.In(circle, () => invites.RevokeMachine("m4")));
        await alice.In(circle, () => invites.RevokeMachine("m4"));
        Assert.Equal(["m1"], (await bob.In(circle, () => invites.ListMachines())).Select(machine => machine.MachineId).ToArray());

        // Hashed at rest: the stored record holds no code, dashed or not.
        var storage = brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.LegacyClusterGrainStorage);
        var state = new GrainState<CircleInvitesRecord>();
        await storage.ReadStateAsync("circles.invites", GrainId.Create("circles.invites", ICircleInvites.Key(circle)), state);
        var stored = JsonSerializer.Serialize(state.State);
        Assert.NotNull(state.State);
        Assert.Equal(3, state.State.Invites.Length);
        foreach (var shown in new[] { code.Code, bound.Code, stale.Code })
        {
            Assert.DoesNotContain(shown, stored, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(shown.Replace("-", "", StringComparison.Ordinal), stored, StringComparison.OrdinalIgnoreCase);
        }
        Assert.All(state.State.Invites, invite => Assert.Equal(43, invite.CodeHash.Length));
    }
}
