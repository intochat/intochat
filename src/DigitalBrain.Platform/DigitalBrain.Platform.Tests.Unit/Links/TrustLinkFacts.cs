using System.Security.Cryptography;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Links;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Links;

// T20-7/T20-8 (R34 replaced, R69 closed): a machine reaches the trust cluster only over its link, and the link's
// authenticated machine id decides what it may write.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class TrustLinkFacts
{
    private static string OwnerBrain => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    [Fact(Timeout = 120_000)]
    public async Task TheFiveTrustClientsAndTheShardsRoundTripOverTheLink()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        using var signer = SequencerFacts.Machine();
        var services = machine.SiloServices;

        var sequencer = services.GetRequiredService<ISequencerClient>();
        Assert.IsType<LinkTrustClients>(sequencer);
        Assert.True(await sequencer.CompareAndSet("p-link", 0, SequencerFacts.Record(signer, "p-link", 1, TimeSpan.FromMinutes(1))));
        Assert.Equal(1, (await sequencer.Get("p-link"))!.Version);
        Assert.Equal(1, (await trust.Grains.GetGrain<ISequencerGrain>("p-link").Get())!.Version);

        var keyring = services.GetRequiredService<IKeyringClient>();
        Assert.Equal(AccountSession.DefaultLogin, await keyring.OwnerOf(OwnerBrain));
        Assert.True(await keyring.TryAdd(OwnerBrain, "link-part", [1, 2, 3]));
        Assert.Equal([1, 2, 3], await keyring.Get(OwnerBrain, "link-part"));

        var directory = services.GetRequiredService<INodeDirectoryClient>();
        var node = (await directory.List()).First().NodeId;
        byte[] shard = [.. "a sealed shard"u8];
        var cid = StorageProof.ContentId(shard);
        await directory.Put(node, cid, shard);
        Assert.Equal(shard, await directory.Get(node, cid));
        Assert.Equal(StorageProof.Compute([7], shard), await directory.Prove(node, cid, [7]));
        Assert.Equal([services.GetRequiredService<MachineIdentity>().MachineId], await trust.Grains.GetGrain<IStorageShard>(StorageShards.Key(node, cid)).Putters());
        await directory.Discard(node, cid);
        Assert.Null(await directory.Get(node, cid));
        Assert.NotNull(await directory.Machine(services.GetRequiredService<MachineIdentity>().MachineId));

        Assert.NotNull(await services.GetRequiredService<IMarketplaceClient>().List());
        Assert.NotNull(await services.GetRequiredService<IIdentityClient>().Membership(OwnerBrain, AccountSession.DefaultLogin));
    }

    [Fact(Timeout = 120_000)]
    public async Task AnUnregisteredMachineIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var stranger = await RawMachine.Dial(trust, ct);
        using var signer = SequencerFacts.Machine();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stranger.Call<(string, long, HeadRecord), bool>(TrustOperations.CompareAndSet,
            ("p-stranger", 0, SequencerFacts.Record(signer, "p-stranger", 1, TimeSpan.FromMinutes(1))), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stranger.Call<(string, string, byte[]), bool>(TrustOperations.KeyAdd, (OwnerBrain, "x", [1]), ct));
        byte[] shard = [9];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stranger.Call<(string, string, byte[]), bool>(TrustOperations.ShardPut,
            ("seed-0", StorageProof.ContentId(shard), shard), ct));

        // The registration-only link may still register itself, and nothing else (T20-4).
        Assert.False(string.IsNullOrEmpty(await stranger.Call<bool, string>(TrustOperations.Deployment, true, ct)));
        await stranger.Register("stranger-account", ct);
        Assert.NotNull(await trust.Grains.GetGrain<IMachineRegistry>(stranger.MachineId).Read());
    }

    // Fix round 1: before a link's machine is registered, the trust host reads at most a registration-sized frame
    // and a couple of calls at a time; registered, the full frame size applies.
    [Fact(Timeout = 120_000)]
    public async Task AnUnregisteredPeerCannotBufferMoreThanTheRegistrationFrameCap()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var stranger = await RawMachine.Dial(trust, ct);
        var large = new byte[64 * 1024];

        await Assert.ThrowsAsync<LinkFailedException>(() => stranger.Send(TrustOperations.Deployment, large, ct));
        Assert.False(string.IsNullOrEmpty(await stranger.Call<bool, string>(TrustOperations.Deployment, true, ct)));
        await stranger.Register("stranger-account", ct);
        var shard = RandomNumberGenerator.GetBytes(large.Length);
        Assert.True(await stranger.Call<(string, string, byte[]), bool>(TrustOperations.ShardPut, ("seed-0", StorageProof.ContentId(shard), shard), ct));
    }

    // T20-8 replaces R72's pairing gate: a machine whose link is registration-only cannot claim a username.
    [Fact(Timeout = 120_000)]
    public async Task AnUnregisteredMachinesLinkCannotRegisterAUsername()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var stranger = await RawMachine.Dial(trust, ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => stranger.Call<MachineSigned<DigitalBrain.Platform.Identity.Authority.PrincipalRegistration>, BrainProvision>(
            TrustOperations.RegisterPrincipal, stranger.Sign(new DigitalBrain.Platform.Identity.Authority.PrincipalRegistration("eve", "Eve"), "eve"), ct));
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        Assert.Null(await machine.SiloServices.GetRequiredService<IIdentityClient>().RegisteredBy("eve"));
    }

    // R99: registration stays open in Stage B, but one source address binds at most one new account a minute.
    [Fact(Timeout = 120_000)]
    public async Task ASecondNewAccountRegistrationFromTheSameAddressWithinAMinuteIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct, registrationsPerAddressPerMinute: 1);
        await using var first = await RawMachine.Dial(trust, ct);
        await using var second = await RawMachine.Dial(trust, ct);

        await first.Register("first-account", ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => second.Register("second-account", ct));
        // Joining an account that already exists is not a new account.
        await second.Register("first-account", ct);
    }

    // R99: what the trust host remembers of a link's registration goes with the link.
    [Fact(Timeout = 120_000)]
    public async Task AClosedLinkIsForgottenByTheRegistry()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        var endpoint = trust.Services.GetRequiredService<TrustLinkEndpoint>();
        var machine = await RawMachine.Dial(trust, ct);
        await machine.Register("forgotten", ct);
        await machine.Call<bool, int>(TrustOperations.RelayPort, true, ct);
        Assert.True(endpoint.Remembers(machine.MachineId));
        var served = endpoint.LinkOf(machine.MachineId)!;

        await machine.DisposeAsync();
        await served.Closed.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.False(endpoint.Remembers(machine.MachineId));
    }

    // R97: the account `platform` owns the seed nodes; no machine and no principal can claim it.
    [Fact(Timeout = 120_000)]
    public async Task TheAccountPlatformCannotBeClaimedOverTheLink()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var mallory = await RawMachine.Dial(trust, ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Register(SeedNodeRegistration.Owner, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.ProvisionOwner(SeedNodeRegistration.Owner, ct));
        await mallory.Register("mallory", ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<MachineSigned<Member>, bool>(TrustOperations.ProvisionOwner,
            mallory.Sign(new Member { PrincipalId = SeedNodeRegistration.Owner, AccountId = "mallory", BrainId = "mallory", Role = MemberRole.Owner, DisplayName = "p" }, "mallory"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<MachineSigned<DigitalBrain.Platform.Identity.Authority.PrincipalRegistration>, BrainProvision>(
            TrustOperations.RegisterPrincipal, mallory.Sign(new DigitalBrain.Platform.Identity.Authority.PrincipalRegistration(SeedNodeRegistration.Owner, "p"), SeedNodeRegistration.Owner), ct));
        Assert.DoesNotContain(SeedNodeRegistration.Owner, (await trust.Grains.GetGrain<IMachineRegistry>(mallory.MachineId).Read())!.Accounts);
    }

    [Fact(Timeout = 120_000)]
    public async Task AKeyringWriteForAnotherAccountsBrainIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        await using var mallory = await RawMachine.Dial(trust, ct);
        await mallory.ProvisionOwner("mallory", ct);
        var mallorysBrain = BrainScope.Create("mallory", "mallory").Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => machine.SiloServices.GetRequiredService<IKeyringClient>().TryAdd(mallorysBrain, "p", [1]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<(string, string, byte[]), bool>(TrustOperations.KeyAdd, (OwnerBrain, "p", [1]), ct));
        Assert.True(await mallory.Call<(string, string, byte[]), bool>(TrustOperations.KeyAdd, (mallorysBrain, "p", [1]), ct));
    }

    // R95: wrapped keys, memberships, grants and a principal's brains are read only by a machine bound to the
    // account they concern.
    [Fact(Timeout = 120_000)]
    public async Task AMachineOfAnotherAccountReadsNeitherItsWrappedKeysNorItsMembers()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        Assert.True(await machine.SiloServices.GetRequiredService<IKeyringClient>().TryAdd(OwnerBrain, "kept", [1]));
        await using var mallory = await RawMachine.Dial(trust, ct);
        await mallory.ProvisionOwner("mallory", ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<(string, string), byte[]?>(TrustOperations.KeyGet, (OwnerBrain, "kept"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<(string, string), Member?>(TrustOperations.Membership, (OwnerBrain, AccountSession.DefaultLogin), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<string, DigitalBrain.Platform.Identity.Grant[]>(TrustOperations.ListGrants, OwnerBrain, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<string, string[]>(TrustOperations.BrainsOf, AccountSession.DefaultLogin, ct));
        // Its own account's reads still answer.
        var mallorysBrain = BrainScope.Create("mallory", "mallory").Id;
        Assert.NotNull(await mallory.Call<(string, string), Member?>(TrustOperations.Membership, (mallorysBrain, "mallory"), ct));
        Assert.NotEmpty(await mallory.Call<string, string[]>(TrustOperations.BrainsOf, "mallory", ct));
        Assert.Equal([1], await machine.SiloServices.GetRequiredService<IKeyringClient>().Get(OwnerBrain, "kept"));
    }

    // R98: a member account's machine reads its own membership of a brain and the keys wrapped for that brain.
    [Fact(Timeout = 120_000)]
    public async Task AMembersMachineReadsItsOwnMembershipAndKeys()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var owner = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        Assert.True(await owner.SiloServices.GetRequiredService<IKeyringClient>().TryAdd(OwnerBrain, "shared", [7]));
        await owner.SiloServices.GetRequiredService<IIdentityClient>().EnsureMember(new Member
        { PrincipalId = "bob", AccountId = AccountSession.DefaultLogin, BrainId = AccountSession.DefaultLogin, Role = MemberRole.Member, DisplayName = "Bob" });
        await using var bob = await RawMachine.Dial(trust, ct);
        await bob.Register("bob", ct);

        Assert.Equal(MemberRole.Member, (await bob.Call<(string, string), Member?>(TrustOperations.Membership, (OwnerBrain, "bob"), ct))?.Role);
        Assert.Equal([7], await bob.Call<(string, string), byte[]?>(TrustOperations.KeyGet, (OwnerBrain, "shared"), ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task AStrangersMachineStillCannotReadThem()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var owner = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        Assert.True(await owner.SiloServices.GetRequiredService<IKeyringClient>().TryAdd(OwnerBrain, "shared", [7]));
        await owner.SiloServices.GetRequiredService<IIdentityClient>().EnsureMember(new Member
        { PrincipalId = "bob", AccountId = AccountSession.DefaultLogin, BrainId = AccountSession.DefaultLogin, Role = MemberRole.Member, DisplayName = "Bob" });
        await using var eve = await RawMachine.Dial(trust, ct);
        await eve.Register("eve", ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eve.Call<(string, string), Member?>(TrustOperations.Membership, (OwnerBrain, "bob"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => eve.Call<(string, string), byte[]?>(TrustOperations.KeyGet, (OwnerBrain, "shared"), ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ANodeDirectoryWriteForAnotherAccountsNodeIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        await using var mallory = await RawMachine.Dial(trust, ct);
        await mallory.Register("mallory", ct);
        var directory = machine.SiloServices.GetRequiredService<INodeDirectoryClient>();
        var machineId = machine.SiloServices.GetRequiredService<MachineIdentity>().MachineId;
        var seed = (await directory.List()).First(node => node.MachineId is null).NodeId;

        // T23-5: only signed registrations of the link's own node, for an account it serves; no seed, no heartbeat for another's node.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            mallory.Sign(new NodeRegistration(seed, mallory.MachineId, "mallory", 1000), "mallory"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            mallory.Sign(new NodeRegistration(StorageNodes.IdOf(mallory.MachineId), mallory.MachineId, AccountSession.DefaultLogin, 1000), AccountSession.DefaultLogin), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<(string, long), bool>(TrustOperations.NodeHeartbeat, (seed, 0L), ct));
        await mallory.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            mallory.Sign(new NodeRegistration(StorageNodes.IdOf(mallory.MachineId), mallory.MachineId, "mallory", 1000), "mallory"), ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.Heartbeat(StorageNodes.IdOf(mallory.MachineId), 0));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ReportAudit(new AuditReport(StorageNodes.IdOf(mallory.MachineId), "sha256:00", 0, false, 16)));
        Assert.Equal("mallory", (await directory.List()).Single(node => node.NodeId == StorageNodes.IdOf(mallory.MachineId)).OwnerAccount);
        Assert.NotEqual(machineId, mallory.MachineId);
        Assert.True((await directory.List()).Single(node => node.NodeId == seed).Online);
    }

    [Fact(Timeout = 120_000)]
    public async Task AShardIsDiscardedOnlyByItsPuttersAccount()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        await using var mallory = await RawMachine.Dial(trust, ct);
        await mallory.Register("mallory", ct);
        var directory = machine.SiloServices.GetRequiredService<INodeDirectoryClient>();
        var node = (await directory.List()).First().NodeId;
        byte[] shard = [.. "the owner's shard"u8];
        var cid = StorageProof.ContentId(shard);
        await directory.Put(node, cid, shard);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mallory.Call<(string, string), bool>(TrustOperations.ShardDiscard, (node, cid), ct));
        Assert.Equal(shard, await directory.Get(node, cid));
        // A machine of another account that puts the same bytes first only adds its own claim: its discard drops
        // that claim, never the owner's (fix round 1: no squatting a cid).
        byte[] squatted = [.. "squatted first"u8];
        var squattedCid = StorageProof.ContentId(squatted);
        await mallory.Call<(string, string, byte[]), bool>(TrustOperations.ShardPut, (node, squattedCid, squatted), ct);
        await directory.Put(node, squattedCid, squatted);
        await mallory.Call<(string, string), bool>(TrustOperations.ShardDiscard, (node, squattedCid), ct);
        Assert.Equal(squatted, await directory.Get(node, squattedCid));
        await directory.Discard(node, cid);
        Assert.Null(await directory.Get(node, cid));
        await directory.Discard(node, squattedCid);
        Assert.Null(await directory.Get(node, squattedCid));
    }

    [Fact(Timeout = 120_000)]
    public async Task AShardPutToAnotherAccountsNodeIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);
        await using var mallory = await RawMachine.Dial(trust, ct);
        await mallory.Register("mallory", ct);
        var mallorys = StorageNodes.IdOf(mallory.MachineId);
        await mallory.Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned,
            mallory.Sign(new NodeRegistration(mallorys, mallory.MachineId, "mallory", 1000), "mallory"), ct);
        byte[] shard = [.. "not for mallory's node"u8];

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => machine.SiloServices.GetRequiredService<INodeDirectoryClient>()
            .Put(mallorys, StorageProof.ContentId(shard), shard));
        Assert.Null(await trust.Grains.GetGrain<IStorageShard>(StorageShards.Key(mallorys, StorageProof.ContentId(shard))).Get());
    }

    [Fact(Timeout = 120_000)]
    public async Task AMachineRefusesATrustHostWhosePinChanged()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        // This machine met another trust host before (TOFU, T20-3).
        await File.WriteAllTextAsync(Path.Combine(directory, "trust.pin"), "0123456789abcdef", ct);
        await using var trust = await TrustHost.StartAsync(ct);

        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var machine = await TestBrain.Create().WithTrust(trust)
                .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.MachineDirectory)}", directory).StartAsync(ct);
        });

        Assert.Contains(nameof(TrustPinMismatchException), Describe(refused), StringComparison.Ordinal);
        Assert.Equal("0123456789abcdef", await File.ReadAllTextAsync(Path.Combine(directory, "trust.pin"), ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task APayloadSignedByAnotherMachineIsRefusedOnThisLink()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await RawMachine.Dial(trust, ct);
        await using var b = await RawMachine.Dial(trust, ct);
        await a.Register("shared", ct);
        await b.Register("shared", ct);
        var account = new Account { AccountId = "shared", OwnerPrincipalId = "shared", Name = "shared", CreatedAt = DateTimeOffset.UtcNow };

        // B's correctly signed payload, sent over A's link.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => a.Call<MachineSigned<Account>, bool>(TrustOperations.EnsureAccount, b.Sign(account, "shared"), ct));
        Assert.True(await b.Call<MachineSigned<Account>, bool>(TrustOperations.EnsureAccount, b.Sign(account, "shared"), ct));
    }

    [Fact(Timeout = 120_000)]
    public async Task ProductionRefusesACloudMachineWithoutATrustPin()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);

        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var machine = await TestBrain.Create().WithTrust(trust)
                .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Cloud))
                .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment())))
                .StartAsync(ct);
        });

        Assert.Contains(TrustPin.PinKey, Describe(refused), StringComparison.Ordinal);
    }

    // R96: with the trust host's id configured, a production machine starts.
    [Fact(Timeout = 120_000)]
    public async Task AProductionMachineWithAPinStarts()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);

        await using var machine = await TestBrain.Create().WithTrust(trust)
            .WithConfiguration(TrustPin.PinKey, trust.TrustId)
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.ConfiguredCustodyAcknowledged)}", "true")
            .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment())))
            .StartAsync(ct);

        Assert.True(machine.SiloServices.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.NotNull(await machine.SiloServices.GetRequiredService<IIdentityClient>().Membership(OwnerBrain, AccountSession.DefaultLogin));
    }

    // The pin's key, shared with the Aspire ring through DigitalBrainNames, is TrustOptions.Pin's.
    [Fact]
    public void ThePinKeyIsTheTrustOptionsPin()
        => Assert.Equal($"{TrustOptions.Key}:{nameof(TrustOptions.Pin)}", TrustPin.PinKey);

    [Fact(Timeout = 120_000)]
    public async Task NoMachineHostOpensAnOrleansClientToTheTrustCluster()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var machine = await TestBrain.Create().WithTrust(trust).StartAsync(ct);

        // The trust clients ride the link, and no Platform type holds a private host (the Orleans client host R34 had).
        Assert.IsType<LinkTrustClients>(machine.SiloServices.GetRequiredService<IIdentityClient>());
        Assert.DoesNotContain(typeof(PlatformHosting).Assembly.GetTypes(), type =>
            type.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters()).Any(parameter => parameter.ParameterType == typeof(IHost))
            || type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Any(field => field.FieldType == typeof(IHost)));
        // The trust grains are not in this silo: a direct grain call cannot reach them.
        await Assert.ThrowsAnyAsync<Exception>(() => machine.Grains.GetGrain<ISequencerGrain>("p-none").Get());
    }

    private static string Describe(Exception error) => error.ToString();

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DigitalBrain.Platform.Tests.Unit";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // A machine key with a bare trust link, for calls a real machine host would never make.
    internal sealed class RawMachine : IAsyncDisposable
    {
        private readonly FileMachineSigningKey _key;
        private readonly QuicMachineLink _link;
        private readonly TrustHost _trust;
        private long _counter;

        private RawMachine(FileMachineSigningKey key, QuicMachineLink link, TrustHost trust) => (_key, _link, _trust) = (key, link, trust);

        public string MachineId => MachineKeys.IdOf(_key.PublicKey);

        public static async Task<RawMachine> Dial(TrustHost trust, CancellationToken cancellationToken)
        {
            var key = Key();
            TrustOptions.TryParse(trust.Endpoint, out var host, out var port);
            var certificate = LinkCertificates.Create(key, "", TimeProvider.System);
            var end = new LinkEnd(() => certificate, null, _ => true, Options, Serializer, Echo, TimeProvider.System);
            var link = await QuicMachineLink.Dial(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port), null, trust.TrustId, end, LinkPath.Direct, cancellationToken);
            return new RawMachine(key, link, trust);
        }

        public Task<LinkReply> Send(string operation, byte[] payload, CancellationToken cancellationToken)
            => _link.Send(new LinkRequest(operation, payload, null, null, Guid.NewGuid()), cancellationToken);

        public async Task<TResult> Call<TArgs, TResult>(string operation, TArgs args, CancellationToken cancellationToken)
            => Serializer.Deserialize<TResult>(LinkReplies.Payload(await _link.Send(new LinkRequest(operation, Serializer.SerializeToArray(args), null, null, Guid.NewGuid()), cancellationToken)))!;

        public async Task Register(string account, CancellationToken cancellationToken)
        {
            var pairing = MachineRegistrar.HashOf(account == AccountSession.DefaultLogin ? _trust.PairingSecret : account + "-pairing");
            await Call<(string, byte[], string, byte[], byte[]), bool>(TrustOperations.RegisterMachine,
                (MachineId, _key.PublicKey, account, pairing, _key.Sign(Signing.Registration(MachineId, _key.PublicKey, account, pairing))), cancellationToken);
        }

        public async Task ProvisionOwner(string account, CancellationToken cancellationToken)
        {
            await Register(account, cancellationToken);
            var owner = new Member { PrincipalId = account, AccountId = account, BrainId = account, Role = MemberRole.Owner, DisplayName = account };
            await Call<MachineSigned<Member>, bool>(TrustOperations.ProvisionOwner, Sign(owner, account), cancellationToken);
        }

        public MachineSigned<T> Sign<T>(T payload, string account)
        {
            var now = DateTimeOffset.UtcNow;
            var counter = Interlocked.Increment(ref _counter);
            return new MachineSigned<T>(payload, account, MachineId, now, counter,
                _key.Sign(MachineSigned<T>.SignedBytes(payload, account, MachineId, now, counter)));
        }

        public async ValueTask DisposeAsync()
        {
            await _link.DisposeAsync();
            _key.Dispose();
        }
    }
}
