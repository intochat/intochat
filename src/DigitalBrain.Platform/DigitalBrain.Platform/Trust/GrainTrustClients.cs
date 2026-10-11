using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Marketplace;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Trust;

// The machine's way to trust grains its own silo hosts (Stage A, tests: no Trust:Endpoint); a machine of a separate
// trust cluster uses LinkTrustClients instead (T20-7). Calls that act for an account are signed by this machine (R37).
internal sealed class GrainTrustClients(Func<IGrainFactory> factory, MachineIdentity? machine, TimeProvider time, MachineCounter? counter)
    : ISequencerClient, INodeDirectoryClient, IKeyringClient, IMarketplaceClient, IIdentityClient
{
    public GrainTrustClients(IGrainFactory grains, MachineIdentity machine, TimeProvider time, MachineCounter counter)
        : this(() => grains, machine, time, counter) { }

    // A host that is not a machine (the MCP): it reads trust state and signs nothing (R70).
    internal static GrainTrustClients Reading(Func<IGrainFactory> grains, TimeProvider time) => new(grains, null, time, null);

    private readonly TrustSigner _signer = new(machine, time, counter);

    private MachineIdentity Identity => _signer.Machine;

    // Facts only: a throwaway counter file per instance, which a production machine must never have (R65).
    internal static GrainTrustClients WithScratchCounter(IGrainFactory grains, MachineIdentity machine) =>
        new(grains, machine, TimeProvider.System, new MachineCounter(Path.Combine(Path.GetTempPath(), $"counter-{Guid.NewGuid():N}")));

    private IGrainFactory Grains => factory();

    // The deployment's pairing secret, hashed (R52): what a ProvisionOwner binds this machine to the account with.
    public byte[] PairingSecretHash { get; init; } = [];

    // Sequencer
    public Task<HeadRecord?> Get(string partHash) => Grains.GetGrain<ISequencerGrain>(partHash).Get();

    public Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next)
        => Grains.GetGrain<ISequencerGrain>(partHash).CompareAndSet(expectedVersion, next);

    // Node directory
    private IStorageNodeDirectory Directory => Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);

    public Task RegisterMachine(string machineId, byte[] publicKey, byte[] agreementKey, string account, byte[] pairingSecretHash, byte[] selfSignature)
        => Grains.GetGrain<IMachineRegistry>(machineId).Register(publicKey, agreementKey, account, pairingSecretHash, selfSignature);

    public Task<string> DeploymentId() => Directory.DeploymentId();

    public Task<RegisteredMachine?> Machine(string machineId) => Grains.GetGrain<IMachineRegistry>(machineId).Read();

    public Task Announce(MachineSigned<MachineCandidates> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return Grains.GetGrain<IMachineRegistry>(candidates.MachineId).Announce(candidates.Call(), candidates.Payload);
    }

    public Task RegisterNode(MachineSigned<NodeRegistration> registration) => Directory.RegisterNode(registration);

    public Task Heartbeat(string nodeId, long usedBytes) => Directory.Heartbeat(Identity.MachineId, nodeId, usedBytes);

    // Stage A: the trust grains are this silo's own, so there is no link machine to bind the report to.
    public Task<AuditReport> ReportAudit(AuditReport report) => Directory.Report(report, null);

    public Task<Placement> Place(int count, string[] exclude, int shardBytes) => Directory.Issue(count, exclude, shardBytes, []);

    Task<StorageNodeView[]> INodeDirectoryClient.List() => Directory.List();

    public Task<StorageCredit[]> PendingCredits(string account) => Grains.GetGrain<IStorageCredits>(account).Pending();

    public Task AcknowledgeCredits(string account, string[] entryIds) => Grains.GetGrain<IStorageCredits>(account).Acknowledge(entryIds);

    public Task Put(string nodeId, string cid, byte[] sealedShard) => Shard(nodeId, cid).Put(sealedShard, machine?.MachineId);

    public Task<byte[]?> Get(string nodeId, string cid) => Shard(nodeId, cid).Get();

    public Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce) => Shard(nodeId, cid).Prove(nonce);

    public Task Discard(string nodeId, string cid) => Shard(nodeId, cid).Drop();

    private IStorageShard Shard(string nodeId, string cid) => Grains.GetGrain<IStorageShard>(StorageShards.Key(nodeId, cid));

    // Keyring
    Task<byte[]?> IKeyringClient.Get(string brainId, string partId) => Grains.GetGrain<IKeyringGrain>(brainId).Get(partId);

    public Task<bool> TryAdd(string brainId, string partId, byte[] wrapped) => Grains.GetGrain<IKeyringGrain>(brainId).TryAdd(partId, wrapped);

    public Task<string?> OwnerOf(string brainId) => Grains.GetGrain<IBrainAuthority>(brainId).Account();

    // Marketplace
    private IMarketplaceCatalog Catalog => Grains.GetGrain<IMarketplaceCatalog>(IMarketplaceCatalog.Key);

    Task<MarketplaceRecord[]> IMarketplaceClient.List() => Catalog.List();

    public Task<MarketplaceRecord?> Read(string publisher, string name) => Catalog.Read(publisher, name);

    public Task Upsert(string account, MarketplaceRecord listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return Signed(listing, account, Catalog.Upsert);
    }

    public Task Remove(string account, string publisher, string name)
        => Signed(new MarketplaceRemoval(publisher, name), account, Catalog.Remove);

    private Task Signed<T>(T payload, string account, Func<MachineSigned<T>, Task> send) => _signer.Signed(payload, account, send);

    private Task<TResult> Signed<T, TResult>(T payload, string account, Func<MachineSigned<T>, Task<TResult>> send) => _signer.Signed(payload, account, send);

    // Identity
    private IAccount Account(string accountId) => Grains.GetGrain<IAccount>(accountId);

    private IPrincipal Principal(string principalId) => Grains.GetGrain<IPrincipal>(principalId);

    private IBrainAuthority Authority(string brainScopeId) => Grains.GetGrain<IBrainAuthority>(brainScopeId);

    public Task<Account?> ReadAccount(string accountId) => Account(accountId).Read();

    private IIdentityWrites Writes => Grains.GetGrain<IIdentityWrites>(IIdentityWrites.Key);

    public Task EnsureAccount(Account account) => Signed(account, account.AccountId, Writes.EnsureAccount);

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName)
        => Signed(new BrainRequest(accountId, principalId, operationId, displayName), accountId, Writes.CreateBrain);

    public async Task<BrainProvision> EnsureBrain(Member owner) => new(owner, await Signed(owner, owner.AccountId, Writes.EnsureBrain));

    // R63: the provisioning machine binds itself to the account first (first registrant wins, R52), then provisions
    // the owner in a call signed for that account.
    public async Task<BrainProvision> ProvisionOwner(Member owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await RegisterMachine(Identity.MachineId, Identity.PublicKey, Identity.AgreementKey, owner.AccountId, PairingSecretHash, _signer.Registration(owner.AccountId, PairingSecretHash));
        return new(owner, await Signed(owner, owner.AccountId, Writes.ProvisionOwner));
    }

    public Task<bool> IsRegistered(string principalId) => Principal(principalId).IsRegistered();

    public Task<string?> RegisteredBy(string principalId) => Principal(principalId).RegisteredBy();

    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName)
        => Signed(new PrincipalRegistration(principalId, displayName), principalId, Writes.RegisterPrincipal);

    public Task<BrainProvision?> CompleteRegistration(string principalId)
        => Signed(new PrincipalRegistration(principalId, ""), principalId, Writes.CompleteRegistration);

    public Task<string[]> BrainsOf(string principalId) => Principal(principalId).Brains();

    public Task<Member?> Membership(string brainScopeId, string principalId) => Authority(brainScopeId).Membership(principalId);

    public Task<Member?> EnsureMember(Member member) => Signed(member, member.AccountId, Writes.EnsureMember);

    // R68: the decision is an unsigned read; only an allowed decision resting on a Once grant spends it, signed.
    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = await Authority(BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id).Authorize(request);
        return decision is { Allowed: true, ConsumesOnce: true }
            ? await Signed(request, request.Caller.AccountId, Writes.Consume)
            : decision;
    }

    public Task<Grant[]> ListGrants(string brainScopeId) => Authority(brainScopeId).ListGrants();

    public Task<Grant> GrantAsOwner(CallerContext caller, Grant grant)
        => Signed(new OwnerGrant(caller, grant), caller.AccountId, Writes.GrantAsOwner);

    public Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode)
        => Signed(new OwnerRevoke(caller, appId, semanticTypeId, mode), caller.AccountId, Writes.RevokeAsOwner);
}
