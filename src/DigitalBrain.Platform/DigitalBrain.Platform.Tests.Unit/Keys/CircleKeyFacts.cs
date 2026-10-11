using System.Buffers.Binary;
using System.Security.Cryptography;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Keys;

// T26-5/R127/R131: a circle brain's part keys are wrapped to each contributed machine's agreement key. Own machines A
// (alice, the creator), B (bob) and C (carol) of one trust cluster; the circle's core and its CircleRoom app part.
public sealed class CircleKeyFacts
{
    private const string Room = "circleroom";

    [Fact(Timeout = 180_000)]
    public async Task AMemberMachineUnwrapsTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var core = PartId.Core(circle.Id);
        var grain = $"notes/{circle.Id}/notes";
        await (await circle.A.Logs.For(core)).Append(grain, "notes", RecordChange.Write, [7], []);

        await circle.Join(circle.B, "bob");

        Assert.Equal(await circle.A.Keys.Unwrap(core, circle.Id, ct), await circle.B.Keys.Unwrap(core, circle.Id, ct));
        await circle.A.Logs.Release(core);
        Assert.Equal([7], (await circle.B.Logs.For(core)).Read(grain, "notes")!.Record);
    }

    [Fact(Timeout = 180_000)]
    public async Task ARemovedMemberCannotReadAnythingWrittenAfterTheRotation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var core = PartId.Core(circle.Id);
        var grain = $"notes/{circle.Id}/notes";
        var log = await circle.A.Logs.For(core);
        await log.Append(grain, "notes", RecordChange.Write, [1], []);
        await circle.Join(circle.B, "bob");
        var old = await circle.B.Keys.Unwrap(core, circle.Id, ct);

        await circle.Remove(circle.B, "bob");
        await log.Append(grain, "notes", RecordChange.Write, [2], []);
        await log.Flushed();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => circle.B.Keys.Unwrap(core, circle.Id, ct));
        Assert.Equal([old], await circle.B.Keys.Held(core, circle.Id, ct));
        var head = (await circle.B.Services.GetRequiredService<ISequencerClient>().Get(HeadRecord.HashOf(core.Value)))!;
        Assert.ThrowsAny<CryptographicException>(() => SegmentSeal.Open(old, HeadContext(core), head.Payload));
        await circle.A.Logs.Release(core);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => circle.B.Logs.For(core));
        Assert.Equal([2], (await circle.A.Logs.For(core)).Read(grain, "notes")!.Record);
    }

    [Fact(Timeout = 180_000)]
    public async Task AWrapIsBoundToItsCircleAndPart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var core = PartId.Core(circle.Id);
        await circle.A.Logs.For(core);
        await circle.Join(circle.B, "bob");

        var wrap = await circle.WrapOf(core, circle.B);
        var agreement = circle.B.Machine.Agreement;

        Assert.Equal(await circle.A.Keys.Unwrap(core, circle.Id, ct), CirclePartKeys.Open(agreement, core, circle.Id, wrap));
        Assert.ThrowsAny<CryptographicException>(() => CirclePartKeys.Open(agreement, PartId.ForApp(circle.Id, Room), circle.Id, wrap));
        Assert.ThrowsAny<CryptographicException>(() => CirclePartKeys.Open(agreement, PartId.Core("circle-other"), "circle-other", wrap));
    }

    [Fact(Timeout = 180_000)]
    public async Task ACloudMachineIsRefusedEveryCirclePart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        await using var cloud = await TestBrain.Create().WithTrust(circle.Trust).AsMachine()
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Cloud)).StartAsync(ct);
        var logs = cloud.Services.GetRequiredService<PartLogs>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => logs.For(PartId.Core(circle.Id)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => logs.For(PartId.ForApp(circle.Id, Room)));
        Assert.DoesNotContain(logs.Opened, part => part.BrainId == circle.Id);
    }

    [Fact(Timeout = 180_000)]
    public async Task AContributedMachineReceivesEveryExistingPartKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var (core, room) = (PartId.Core(circle.Id), PartId.ForApp(circle.Id, Room));
        await circle.A.Logs.For(core);
        await circle.A.Logs.For(room);

        await circle.Join(circle.C, "carol");

        Assert.Equal(await circle.A.Keys.Unwrap(core, circle.Id, ct), await circle.C.Keys.Unwrap(core, circle.Id, ct));
        Assert.Equal(await circle.A.Keys.Unwrap(room, circle.Id, ct), await circle.C.Keys.Unwrap(room, circle.Id, ct));
        Assert.NotEqual(await circle.C.Keys.Unwrap(core, circle.Id, ct), await circle.C.Keys.Unwrap(room, circle.Id, ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task AWrapIsEphemeralStaticToTheAgreementKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var core = PartId.Core(circle.Id);
        await circle.A.Logs.For(core);
        await circle.Join(circle.B, "bob");
        await circle.Join(circle.C, "carol");

        var (toA, toB, toC) = (await circle.WrapOf(core, circle.A), await circle.WrapOf(core, circle.B), await circle.WrapOf(core, circle.C));
        byte[][] ephemeral = [Ephemeral(toA), Ephemeral(toB), Ephemeral(toC)];

        Assert.Equal(3, ephemeral.Distinct(Bytes.Comparer).Count());
        foreach (var machine in new[] { circle.A, circle.B, circle.C })
        {
            Assert.DoesNotContain(machine.Machine.AgreementKey, ephemeral, Bytes.Comparer);
            Assert.DoesNotContain(machine.Machine.PublicKey, ephemeral, Bytes.Comparer);
        }
        var key = await circle.A.Keys.Unwrap(core, circle.Id, ct);
        Assert.Equal(key, CirclePartKeys.Open(circle.B.Machine.Agreement, core, circle.Id, toB));
        Assert.Equal(key, CirclePartKeys.Open(circle.C.Machine.Agreement, core, circle.Id, toC));
        Assert.ThrowsAny<CryptographicException>(() => CirclePartKeys.Open(circle.C.Machine.Agreement, core, circle.Id, toB));
        Assert.ThrowsAny<CryptographicException>(() => CirclePartKeys.Open(circle.A.Machine.Agreement, core, circle.Id, toB));
    }

    [Fact(Timeout = 180_000)]
    public async Task DataWrittenBeforeTheRotationStaysReadableToTheRemovedMachine()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var circle = await Circle.Start(ct);
        var core = PartId.Core(circle.Id);
        var log = await circle.A.Logs.For(core);
        await log.Append($"notes/{circle.Id}/notes", "notes", RecordChange.Write, [1], []);
        await log.Flushed();
        await circle.Join(circle.B, "bob");
        var sequencer = circle.B.Services.GetRequiredService<ISequencerClient>();
        var before = (await sequencer.Get(HeadRecord.HashOf(core.Value)))!.Payload;

        await circle.Remove(circle.B, "bob");

        var held = Assert.Single(await circle.B.Keys.Held(core, circle.Id, ct));
        var serializer = circle.B.Services.GetRequiredService<Orleans.Serialization.Serializer>();
        var payload = serializer.Deserialize<HeadPayload>(SegmentSeal.Open(held, HeadContext(core), before))!;
        Assert.True(payload.LastSeq >= 1);
        var after = (await sequencer.Get(HeadRecord.HashOf(core.Value)))!.Payload;
        Assert.ThrowsAny<CryptographicException>(() => SegmentSeal.Open(held, HeadContext(core), after));
    }

    private static string HeadContext(PartId part) => $"part-head|{part.Value}";

    private static byte[] Ephemeral(byte[] wrap) => wrap.AsSpan(4, BinaryPrimitives.ReadInt32LittleEndian(wrap)).ToArray();

    private sealed class Bytes : IEqualityComparer<byte[]>
    {
        public static readonly Bytes Comparer = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }

    // One own machine of the fact: its part logs, circle keys and key sharing.
    private sealed record Host(ITestBrain Brain)
    {
        public IServiceProvider Services => Brain.Services;
        public PartLogs Logs => Services.GetRequiredService<PartLogs>();
        public CirclePartKeys Keys => Services.GetRequiredService<CirclePartKeys>();
        public ICirclePartKeys Sharing => Services.GetRequiredService<ICirclePartKeys>();
        public IIdentityClient Identity => Services.GetRequiredService<IIdentityClient>();
        public MachineIdentity Machine => Services.GetRequiredService<MachineIdentity>();
    }

    // A circle created on A for the default owner account (alice); B and C join as bob and carol in the order
    // CircleNeuron runs Accept and Remove on the core holder: membership, the contributed machine, then the keys.
    private sealed class Circle : IAsyncDisposable
    {
        private Circle(TrustHost trust, Host a, Host b, Host c, string id) => (Trust, A, B, C, Id) = (trust, a, b, c, id);

        public TrustHost Trust { get; }
        public Host A { get; }
        public Host B { get; }
        public Host C { get; }
        public string Id { get; }

        public static async Task<Circle> Start(CancellationToken ct)
        {
            var trust = await TrustHost.StartAsync(ct);
            var a = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var b = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var c = new Host(await TestBrain.Create().WithTrust(trust).AsMachine().StartAsync(ct));
            var alice = AccountSession.DefaultLogin;
            await a.Identity.EnsureAccount(new Account { AccountId = alice, Name = alice, OwnerPrincipalId = alice });
            var id = (await a.Identity.CreateBrain(alice, alice, Guid.NewGuid().ToString("N"), "friends", BrainKind.Circle)).Member.BrainId;
            return new Circle(trust, a, b, c, id);
        }

        public async Task Join(Host machine, string account)
        {
            await machine.Services.GetRequiredService<MachineRegistrar>().Bind(account);
            await machine.Identity.EnsureMember(new Member { PrincipalId = account, AccountId = account, BrainId = Id, Role = MemberRole.Member, DisplayName = account });
            await machine.Identity.ContributeMachine(Id, machine.Machine.MachineId);
            await A.Sharing.ShareWith(Id, machine.Machine.MachineId);
        }

        public async Task Remove(Host machine, string account)
        {
            await A.Identity.RemoveMember(Id, account);
            await A.Identity.WithdrawMachine(Id, machine.Machine.MachineId);
            await A.Sharing.Rotate(Id);
        }

        public async Task<byte[]> WrapOf(PartId part, Host machine)
            => (await machine.Services.GetRequiredService<IKeyringClient>().WrapsOf(Id, part.Value, machine.Machine.MachineId))[0].Wrapped;

        public async ValueTask DisposeAsync()
        {
            await A.Brain.DisposeAsync();
            await B.Brain.DisposeAsync();
            await C.Brain.DisposeAsync();
            await Trust.DisposeAsync();
        }
    }
}
