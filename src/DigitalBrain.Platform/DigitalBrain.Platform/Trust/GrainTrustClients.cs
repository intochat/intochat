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

// The machine's only way to the trust grains (Stage A: direct grain calls on the given factory; with a gateway, the
// private trust client's). Calls that act for an account are signed by this machine (R37).
internal sealed class GrainTrustClients(Func<IGrainFactory> factory, MachineIdentity machine, TimeProvider time)
    : ISequencerClient, INodeDirectoryClient, IKeyringClient, IMarketplaceClient, IIdentityClient
{
    public GrainTrustClients(IGrainFactory grains, MachineIdentity machine, TimeProvider time) : this(() => grains, machine, time) { }

    // Resolved per call: a gateway's client exists only once the silo has initialized it.
    private IGrainFactory Grains => factory();

    private readonly SemaphoreSlim _signing = new(1, 1);
    private long _counter;

    // Sequencer
    public Task<HeadRecord?> Get(string partHash) => Grains.GetGrain<ISequencerGrain>(partHash).Get();

    public Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next)
        => Grains.GetGrain<ISequencerGrain>(partHash).CompareAndSet(expectedVersion, next);

    // Node directory
    private IStorageNodeDirectory Directory => Grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);

    public Task RegisterMachine(string machineId, byte[] publicKey, string account, byte[] pairingSecretHash, byte[] selfSignature)
        => Grains.GetGrain<IMachineRegistry>(machineId).Register(publicKey, account, pairingSecretHash, selfSignature);

    public Task Register(string nodeId, string ownerAccount) => Directory.Register(nodeId, ownerAccount);

    public Task SetOnline(string nodeId, bool online) => Directory.SetOnline(nodeId, online);

    public Task ReportAudit(string nodeId, bool passed) => Directory.ReportAudit(nodeId, passed);

    public Task<string[]> Place(int count, string[] exclude) => Directory.Place(count, exclude);

    Task<StorageNodeView[]> INodeDirectoryClient.List() => Directory.List();

    public Task Put(string nodeId, string cid, byte[] sealedShard) => Shard(nodeId, cid).Put(sealedShard);

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

    // Signed calls go one at a time so the trust side sees this machine's counter strictly rising.
    private async Task Signed<T>(T payload, string account, Func<MachineSigned<T>, Task> send)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        await _signing.WaitAsync();
        try
        {
            var now = time.GetUtcNow();
            // Seeded from the clock, so a restarted machine keeps counting upwards without storing the counter.
            _counter = Math.Max(_counter + 1, now.ToUnixTimeMilliseconds() * 1000);
            var signature = machine.Sign(MachineSigned<T>.SignedBytes(payload, account, machine.MachineId, now, _counter));
            await send(new MachineSigned<T>(payload, account, machine.MachineId, now, _counter, signature));
        }
        finally { _signing.Release(); }
    }

    // Identity
    private IAccount Account(string accountId) => Grains.GetGrain<IAccount>(accountId);

    private IPrincipal Principal(string principalId) => Grains.GetGrain<IPrincipal>(principalId);

    private IBrainAuthority Authority(string brainScopeId) => Grains.GetGrain<IBrainAuthority>(brainScopeId);

    public Task<Account?> ReadAccount(string accountId) => Account(accountId).Read();

    public Task EnsureAccount(Account account) => Account(account.AccountId).EnsureCreated(account);

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName)
        => Account(accountId).CreateBrain(principalId, operationId, displayName);

    public async Task<BrainProvision> EnsureBrain(Member owner) => new(owner, await Account(owner.AccountId).EnsureBrain(owner));

    public async Task<BrainProvision> ProvisionOwner(Member owner)
        => new(owner, await Authority(BrainScope.Create(owner.AccountId, owner.BrainId).Id).ProvisionOwner(owner));

    public Task<PrincipalCredential?> Credential(string principalId) => Principal(principalId).Credential();

    public Task<BrainProvision> RegisterPrincipal(string principalId, string passwordHash, string displayName)
        => Principal(principalId).Register(passwordHash, displayName);

    public Task<BrainProvision?> CompleteRegistration(string principalId) => Principal(principalId).CompleteRegistration();

    public Task SetPasswordHash(string principalId, string passwordHash) => Principal(principalId).SetPasswordHash(passwordHash);

    public Task<string[]> BrainsOf(string principalId) => Principal(principalId).Brains();

    public Task<Member?> Membership(string brainScopeId, string principalId) => Authority(brainScopeId).Membership(principalId);

    public async Task<Member?> EnsureMember(Member member)
    {
        var authority = Authority(BrainScope.Create(member.AccountId, member.BrainId).Id);
        await authority.EnsureMember(member);
        return await authority.Membership(member.PrincipalId);
    }

    public Task<CallDecision?> Authorize(CallRequest request)
        => Authority(BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id).Authorize(request);

    public Task<Grant[]> ListGrants(string brainScopeId) => Authority(brainScopeId).ListGrants();

    public Task<Grant> GrantAsOwner(CallerContext caller, Grant grant)
        => Authority(BrainScope.Create(caller.AccountId, caller.BrainId).Id).GrantAsOwner(caller, grant);

    public Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode)
        => Authority(BrainScope.Create(caller.AccountId, caller.BrainId).Id).RevokeAsOwner(caller, appId, semanticTypeId, mode);
}
