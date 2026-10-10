using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Platform.Identity.Grants;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Marketplace;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Tests.Unit.Sequencer;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

public sealed class TrustClientFacts
{
    // The machine-side types that reach trust state; the Apps module's Marketplace neuron and guard are
    // checked by its own suite (MarketplaceClientFacts).
    private static readonly Type[] MachineSide =
    [
        typeof(PartLease), typeof(PartKeys), typeof(MeshStore), typeof(PartLogs),
        typeof(IdentityStartup), typeof(DirectoryBrainAccess), typeof(GrainGrantPolicySource), typeof(IdentityDirectoryNeuron),
        typeof(PrincipalPasswords), typeof(MachineRegistrar), typeof(MachineRegistration),
    ];

    [Fact]
    public void AMachineReachesTrustGrainsOnlyThroughTheClients()
    {
        var reaching = MachineSide
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Cast<MethodBase>()
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.ParameterType == typeof(IGrainFactory) || parameter.ParameterType == typeof(IClusterClient))
                .Select(parameter => $"{type.Name}.{parameter.Member.Name}({parameter.Name})"))
            .ToArray();

        Assert.Empty(reaching);
    }

    // R60: every trust grain that remains in the trust cluster. A grain interface added to Platform.Trust must be
    // listed here, and only GrainTrustClients may address one from the machine's assembly.
    private static readonly Type[] TrustGrains =
    [
        typeof(DigitalBrain.Platform.Sequencer.ISequencerGrain), typeof(IKeyringGrain), typeof(IStorageNodeDirectory), typeof(IStorageShard),
        typeof(IMachineRegistry), typeof(IAccountMachines), typeof(DigitalBrain.Platform.Identity.Authority.IBrainAuthority),
        typeof(DigitalBrain.Platform.Identity.Authority.IAccount), typeof(DigitalBrain.Platform.Identity.Authority.IPrincipal), typeof(IMarketplaceCatalog),
    ];

    [Fact]
    public void OnlyTheClientsAddressATrustGrainFromTheMachine()
    {
        var declared = typeof(TrustHosting).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.GetInterfaces().Any(i => i == typeof(IGrainWithStringKey) || i == typeof(IGrainWithGuidKey) || i == typeof(IGrainWithIntegerKey)))
            .Select(type => type.FullName).Order();
        Assert.Equal(declared, TrustGrains.Select(type => type.FullName).Order());

        var callers = TrustGrainCallers.Find(typeof(GrainTrustClients).Assembly, TrustGrains);
        // The scan sees the clients themselves, so a lone entry means no other caller, not a blind scan.
        Assert.Equal([typeof(GrainTrustClients).FullName!], callers);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheGrainClientsRoundTripAHeadAWrappedKeyAShardAndAListing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var services = brain.SiloServices;
        using var machine = SequencerFacts.Machine();

        var sequencer = services.GetRequiredService<ISequencerClient>();
        Assert.True(await sequencer.CompareAndSet("p1", 0, SequencerFacts.Record(machine, "p1", 1, TimeSpan.FromMinutes(1))));
        Assert.Equal(1, (await sequencer.Get("p1"))!.Version);

        var keyring = services.GetRequiredService<IKeyringClient>();
        Assert.True(await keyring.TryAdd("acct/brain", "acct/brain/apps/x", [1, 2, 3]));
        Assert.Equal([1, 2, 3], await keyring.Get("acct/brain", "acct/brain/apps/x"));

        var directory = services.GetRequiredService<INodeDirectoryClient>();
        await directory.Register("node-a", "someone");
        var shard = Encoding.UTF8.GetBytes("a sealed shard of sixteen or more bytes");
        var cid = StorageProof.ContentId(shard);
        await directory.Put("node-a", cid, shard);
        Assert.Equal(shard, await directory.Get("node-a", cid));
        Assert.Contains(await directory.List(), node => node.NodeId == "node-a");

        await brain.OwnBrainAsync(ModuleTest.DefaultAccount, "home");
        var marketplace = services.GetRequiredService<IMarketplaceClient>();
        var listing = new MarketplaceRecord("owner", ModuleTest.DefaultAccount, "home", "echo", "Echo", "echoes", "1.0.0", null);
        await marketplace.Upsert(ModuleTest.DefaultAccount, listing);
        Assert.Equal(listing, await marketplace.Read("owner", "echo"));
        await marketplace.Remove(ModuleTest.DefaultAccount, "owner", "echo");
        Assert.Empty(await marketplace.List());
    }

    [Fact(Timeout = 60_000)]
    public async Task AMachineRegistersItsKeyOnceAndADifferentKeyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var directory = brain.SiloServices.GetRequiredService<INodeDirectoryClient>();
        using var machine = SequencerFacts.Machine();
        using var other = SequencerFacts.Machine();

        await Register(directory, machine, "alice", []);
        await Register(directory, machine, "alice", []);
        var forged = other.Sign(Signing.Registration(machine.MachineId, other.PublicKey, "alice", []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.RegisterMachine(machine.MachineId, other.PublicKey, "alice", [], forged));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASecondMachineWithTheSamePairingSecretJoinsTheAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var directory = brain.SiloServices.GetRequiredService<INodeDirectoryClient>();
        using var first = SequencerFacts.Machine();
        using var second = SequencerFacts.Machine();
        var pairing = SHA256.HashData(Encoding.UTF8.GetBytes("deployment-secret"));

        await Register(directory, first, "alice", pairing);
        await Register(directory, second, "alice", pairing);

        await Owned(brain, "alice", "home");
        var clients = new GrainTrustClients(brain.Grains, second, TimeProvider.System);
        await clients.Upsert("alice", new MarketplaceRecord("alice", "alice", "home", "echo", "Echo", "", "1.0.0", null));
        Assert.NotNull(await clients.Read("alice", "echo"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASecondMachineWithoutThePairingSecretIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var directory = brain.SiloServices.GetRequiredService<INodeDirectoryClient>();
        using var first = SequencerFacts.Machine();
        using var second = SequencerFacts.Machine();
        using var third = SequencerFacts.Machine();

        await Register(directory, first, "alice", SHA256.HashData(Encoding.UTF8.GetBytes("deployment-secret")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Register(directory, second, "alice", []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Register(directory, third, "alice", SHA256.HashData(Encoding.UTF8.GetBytes("a guess"))));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASignedPayloadForAnotherAccountIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var machine = SequencerFacts.Machine();
        await Register(brain.SiloServices.GetRequiredService<INodeDirectoryClient>(), machine, "mallory", []);
        await Owned(brain, "alice", "home");
        var clients = new GrainTrustClients(brain.Grains, machine, TimeProvider.System);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            clients.Upsert("alice", new MarketplaceRecord("alice", "alice", "home", "echo", "Echo", "", "1.0.0", null)));
        Assert.Null(await clients.Read("alice", "echo"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AReplayedSignedPayloadIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        using var machine = SequencerFacts.Machine();
        await Register(brain.SiloServices.GetRequiredService<INodeDirectoryClient>(), machine, "alice", []);
        await Owned(brain, "alice", "home");
        var catalog = brain.Grains.GetGrain<IMarketplaceCatalog>(IMarketplaceCatalog.Key);
        var listing = new MarketplaceRecord("alice", "alice", "home", "echo", "Echo", "", "1.0.0", null);

        var now = DateTimeOffset.UtcNow;
        var signed = Sign(machine, listing, "alice", now, 10);
        await catalog.Upsert(signed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.Upsert(signed));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => catalog.Upsert(Sign(machine, listing, "alice", now - TimeSpan.FromMinutes(6), 11)));
        await catalog.Upsert(Sign(machine, listing, "alice", now, 12));
    }

    // The brain's owner on record at the trust side, without binding the test host's own machine to the account.
    internal static Task Owned(ModuleBrain brain, string account, string brainId)
        => brain.SiloServices.GetRequiredService<DigitalBrain.Framework.Platform.Identity.IIdentityClient>().ProvisionOwner(new()
        {
            PrincipalId = account, AccountId = account, BrainId = brainId,
            Role = DigitalBrain.Framework.Platform.Identity.MemberRole.Owner, DisplayName = account,
        });

    internal static MachineSigned<T> Sign<T>(MachineIdentity machine, T payload, string account, DateTimeOffset at, long counter)
        => new(payload, account, machine.MachineId, at, counter, machine.Sign(MachineSigned<T>.SignedBytes(payload, account, machine.MachineId, at, counter)));

    internal static Task Register(INodeDirectoryClient directory, MachineIdentity machine, string account, byte[] pairing)
        => directory.RegisterMachine(machine.MachineId, machine.PublicKey, account, pairing,
            machine.Sign(Signing.Registration(machine.MachineId, machine.PublicKey, account, pairing)));
}
