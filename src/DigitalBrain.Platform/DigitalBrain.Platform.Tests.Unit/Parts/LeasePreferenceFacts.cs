using System.Security.Cryptography;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

// Q11/Q17/R128/T26-4: an owner's core never leaves a machine serving its owner; a circle part runs on a machine
// contributed to that circle, preferring the operator's, and returns to it once the part has been quiet.
public sealed class LeasePreferenceFacts
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    [Fact(Timeout = 180_000)]
    public async Task ACircleMachineIsRefusedTheCore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await Machines.Start(ct);
        var personal = await machines.Personal();
        var preference = machines.Preference(machines.B, Quiet);

        Assert.False(await preference.MayHold(PartId.Core(personal), personal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => machines.Lease(machines.B, PartId.Core(personal), preference, TimeProvider.System));
    }

    [Fact(Timeout = 180_000)]
    public async Task ACircleCoreIsHeldOnlyByContributedMachines()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var machines = await Machines.Start(ct);
        var circle = await machines.Circle();
        var core = PartId.Core(circle);

        Assert.True(await machines.Preference(machines.A, Quiet).MayHold(core, circle));
        Assert.True(await machines.Preference(machines.B, Quiet).MayHold(core, circle));
        Assert.False(await machines.Preference(machines.C, Quiet).MayHold(core, circle));
        Assert.False(await machines.Preference(machines.Cloud, CloudOptions).MayHold(core, circle));
    }

    [Fact(Timeout = 180_000)]
    public async Task TheLeaseReturnsAfterThePartGoesQuiet()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var machines = await Machines.Start(ct, time);
        var circle = await machines.Circle();
        var part = PartId.ForApp(circle, "notes");
        var key = RandomNumberGenerator.GetBytes(32);

        // Alice is offline (not the holder): bob's machine B leases the circle part and writes.
        var b = await machines.Lease(machines.B, part, key, machines.Preference(machines.B, Quiet, time), time);
        await b.Commit(new HeadPayload { LastSeq = 7 });

        // Alice is back and operating; the part has been quiet, so B returns it to her machine A.
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(await machines.Preference(machines.B, Quiet, time).ShouldReturn(b, Alice, ct));
        await b.Release();

        var a = await machines.Lease(machines.A, part, key, machines.Preference(machines.A, Quiet, time), time);
        Assert.Equal(7, a.Payload.LastSeq);
    }

    [Fact(Timeout = 180_000)]
    public async Task ABusyPartDoesNotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var machines = await Machines.Start(ct, time);
        var circle = await machines.Circle();
        var part = PartId.ForApp(circle, "notes");

        var b = await machines.Lease(machines.B, part, RandomNumberGenerator.GetBytes(32), machines.Preference(machines.B, Quiet, time), time);
        time.Advance(TimeSpan.FromSeconds(6));
        await b.Commit(new HeadPayload { LastSeq = 1 });

        Assert.False(await machines.Preference(machines.B, Quiet, time).ShouldReturn(b, Alice, ct));
    }

    private static PartsOptions Quiet => new() { Role = MachineRole.Own, QuietFor = TimeSpan.FromSeconds(5) };

    private static PartsOptions CloudOptions => new() { Role = MachineRole.Cloud, QuietFor = TimeSpan.FromSeconds(5) };

    private sealed record Host(ITestBrain Brain)
    {
        public IServiceProvider Services => Brain.Services;
        public IIdentityClient Identity => Services.GetRequiredService<IIdentityClient>();
        public MachineIdentity Machine => Services.GetRequiredService<MachineIdentity>();
    }

    // A trust cluster with three own machines (A, B, C) and one Cloud machine. Alice's account is bound to A only.
    private sealed class Machines : IAsyncDisposable
    {
        private Machines(TrustHost trust, Host a, Host b, Host c, Host cloud) => (Trust, A, B, C, Cloud) = (trust, a, b, c, cloud);

        public TrustHost Trust { get; }
        public Host A { get; }
        public Host B { get; }
        public Host C { get; }
        public Host Cloud { get; }

        public static async Task<Machines> Start(CancellationToken ct, TimeProvider? trustTime = null)
        {
            var trust = await TrustHost.StartAsync(ct, configure: trustTime is null ? null : silo => silo.Services.AddSingleton(trustTime));
            var a = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var b = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var c = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var cloud = new Host(await TestBrain.Create().WithTrust(trust).AsMachine()
                .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Cloud)).StartAsync(ct));
            await a.Services.GetRequiredService<MachineRegistrar>().Bind(Alice);
            await a.Identity.EnsureAccount(new Account { AccountId = Alice, Name = Alice, OwnerPrincipalId = Alice });
            return new Machines(trust, a, b, c, cloud);
        }

        public Task<string> Personal()
            => PersonalAsync();

        private async Task<string> PersonalAsync()
            => (await A.Identity.CreateBrain(Alice, Alice, Guid.NewGuid().ToString("N"), "home", BrainKind.Personal)).Member.BrainId;

        // A circle owned by alice with A contributed at creation (R151) and B contributed by bob.
        public async Task<string> Circle()
        {
            var circle = (await A.Identity.CreateBrain(Alice, Alice, Guid.NewGuid().ToString("N"), "friends", BrainKind.Circle)).Member.BrainId;
            await B.Services.GetRequiredService<MachineRegistrar>().Bind(Bob);
            await B.Identity.EnsureMember(new Member { PrincipalId = Bob, AccountId = Bob, BrainId = circle, Role = MemberRole.Member, DisplayName = Bob });
            await B.Identity.ContributeMachine(circle, B.Machine.MachineId);
            return circle;
        }

        public LeasePreference Preference(Host machine, PartsOptions options, TimeProvider? time = null)
            => new(machine.Machine, options, time ?? TimeProvider.System, machine.Identity, machine.Services.GetRequiredService<IKeyringClient>());

        public Task<PartLease> Lease(Host machine, PartId part, LeasePreference preference, TimeProvider time)
            => Lease(machine, part, RandomNumberGenerator.GetBytes(32), preference, time);

        public Task<PartLease> Lease(Host machine, PartId part, byte[] key, LeasePreference preference, TimeProvider time)
            => PartLease.Acquire(part, key, machine.Services.GetRequiredService<ISequencerClient>(), machine.Machine,
                new SeenVersions(Path.Combine(Path.GetTempPath(), $"seen-{Guid.NewGuid():N}.json")), Quiet, time,
                machine.Services.GetRequiredService<Serializer>(), preference);

        public async ValueTask DisposeAsync()
        {
            await A.Brain.DisposeAsync();
            await B.Brain.DisposeAsync();
            await C.Brain.DisposeAsync();
            await Cloud.Brain.DisposeAsync();
            await Trust.DisposeAsync();
        }
    }
}
