using System.Diagnostics;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Framework.Platform.Marketplace;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Authority;
using DigitalBrain.Platform.Keys;
using DigitalBrain.Platform.Marketplace;
using DigitalBrain.Platform.Mesh;
using DigitalBrain.Platform.Sequencer;
using DigitalBrain.Platform.Trust;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Links;

// The five trust clients and the shard calls over the machine's trust link (T20-7, R34 replaced): one LinkRequest per
// call, answered by the trust host's TrustLinkEndpoint for this link's authenticated machine. Calls that act for an
// account still travel signed (R37, R63); the trust side honours them only when the signer is the link's machine.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed class LinkTrustClients(QuicMachineTransport transport, Serializer serializer, TrustSigner signer)
    : ISequencerClient, INodeDirectoryClient, IKeyringClient, IMarketplaceClient, IIdentityClient
{
    public static readonly ActivitySource Source = new("DigitalBrain.Machine.TrustLink");

    // The deployment's pairing secret, hashed (R52): what a ProvisionOwner binds this machine to the account with.
    public byte[] PairingSecretHash { get; init; } = [];

    private async Task<TResult> Call<TArgs, TResult>(string operation, TArgs args)
    {
        using var activity = Source.StartActivity($"TrustLink {operation}", ActivityKind.Client);
        var parent = Activity.Current;
        var payload = await transport.CallTrust(new LinkRequest(operation, serializer.SerializeToArray(args), parent?.Id, parent?.TraceStateString, Guid.NewGuid()),
            CancellationToken.None).ConfigureAwait(false);
        return serializer.Deserialize<TResult>(payload)!;
    }

    private Task<TResult> Signed<T, TResult>(string operation, T payload, string account)
        => signer.Signed(payload, account, signed => Call<MachineSigned<T>, TResult>(operation, signed));

    private Task Signed<T>(string operation, T payload, string account)
        => signer.Signed(payload, account, signed => Call<MachineSigned<T>, bool>(operation, signed));

    // Sequencer
    public Task<HeadRecord?> Get(string partHash) => Call<string, HeadRecord?>(TrustOperations.SequencerGet, partHash);

    public Task<bool> CompareAndSet(string partHash, long expectedVersion, HeadRecord next, string partId)
        => Call<(string, long, HeadRecord, string), bool>(TrustOperations.CompareAndSet, (partHash, expectedVersion, next, partId));

    public Task Revoke(string circleBrainId, string partId, string machineId)
        => Call<(string, string, string), bool>(TrustOperations.SequencerRevoke, (circleBrainId, partId, machineId));

    // Node directory
    public Task<string> DeploymentId() => Call<bool, string>(TrustOperations.Deployment, true);

    public Task RegisterMachine(string machineId, byte[] publicKey, byte[] agreementKey, string account, byte[] pairingSecretHash, byte[] selfSignature)
        => Call<(string, byte[], byte[], string, byte[], byte[]), bool>(TrustOperations.RegisterMachine,
            (machineId, publicKey, agreementKey, account, pairingSecretHash, selfSignature));

    public Task<RegisteredMachine?> Machine(string machineId) => Call<string, RegisteredMachine?>(TrustOperations.Machine, machineId);

    public Task Announce(MachineSigned<MachineCandidates> candidates) => Call<MachineSigned<MachineCandidates>, bool>(TrustOperations.Announce, candidates);

    public Task Announce(MachineCandidates candidates, string account) => Signed(TrustOperations.Announce, candidates, account);

    public Task RegisterNode(MachineSigned<NodeRegistration> registration)
        => Call<MachineSigned<NodeRegistration>, bool>(TrustOperations.NodeRegisterSigned, registration);

    public Task Heartbeat(string nodeId, long usedBytes) => Call<(string, long), bool>(TrustOperations.NodeHeartbeat, (nodeId, usedBytes));

    public Task<AuditReport> ReportAudit(AuditReport report) => Call<AuditReport, AuditReport>(TrustOperations.NodeAudit, report);

    public Task<Placement> Place(int count, string[] exclude, int shardBytes)
        => Call<(int, string[], int), Placement>(TrustOperations.NodePlace, (count, exclude, shardBytes));

    Task<StorageNodeView[]> INodeDirectoryClient.List() => Call<bool, StorageNodeView[]>(TrustOperations.NodeList, true);

    public Task<StorageCredit[]> PendingCredits(string account) => Call<string, StorageCredit[]>(TrustOperations.CreditsPending, account);

    public Task AcknowledgeCredits(string account, string[] entryIds)
        => Call<(string, string[]), bool>(TrustOperations.CreditsAcknowledge, (account, entryIds));

    public Task Put(string nodeId, string cid, byte[] sealedShard) => Call<(string, string, byte[]), bool>(TrustOperations.ShardPut, (nodeId, cid, sealedShard));

    public Task<byte[]?> Get(string nodeId, string cid) => Call<(string, string), byte[]?>(TrustOperations.ShardGet, (nodeId, cid));

    public Task<byte[]?> Prove(string nodeId, string cid, byte[] nonce) => Call<(string, string, byte[]), byte[]?>(TrustOperations.ShardProve, (nodeId, cid, nonce));

    public Task Discard(string nodeId, string cid) => Call<(string, string), bool>(TrustOperations.ShardDiscard, (nodeId, cid));

    // Keyring
    Task<byte[]?> IKeyringClient.Get(string brainId, string partId) => Call<(string, string), byte[]?>(TrustOperations.KeyGet, (brainId, partId));

    public Task<bool> TryAdd(string brainId, string partId, byte[] wrapped) => Call<(string, string, byte[]), bool>(TrustOperations.KeyAdd, (brainId, partId, wrapped));

    public Task<CircleKeyWrap[]> WrapsOf(string brainId, string partId, string machineId)
        => Call<(string, string, string), CircleKeyWrap[]>(TrustOperations.KeyWraps, (brainId, partId, machineId));

    public Task<long> Generation(string brainId, string partId) => Call<(string, string), long>(TrustOperations.KeyGeneration, (brainId, partId));

    public Task<string[]> CircleParts(string brainId) => Call<string, string[]>(TrustOperations.KeyCircleParts, brainId);

    public Task<bool> AddWraps(string brainId, string partId, long generation, CircleKeyWrap[] wraps)
        => Call<(string, string, long, CircleKeyWrap[]), bool>(TrustOperations.KeyAddWraps, (brainId, partId, generation, wraps));

    public Task<string?> OwnerOf(string brainId) => Call<string, string?>(TrustOperations.OwnerOf, brainId);

    // Marketplace
    Task<MarketplaceRecord[]> IMarketplaceClient.List() => Call<bool, MarketplaceRecord[]>(TrustOperations.MarketList, true);

    public Task<MarketplaceRecord?> Read(string publisher, string name) => Call<(string, string), MarketplaceRecord?>(TrustOperations.MarketRead, (publisher, name));

    public Task Upsert(string account, MarketplaceRecord listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return Signed(TrustOperations.MarketUpsert, listing, account);
    }

    public Task Remove(string account, string publisher, string name) => Signed(TrustOperations.MarketRemove, new MarketplaceRemoval(publisher, name), account);

    // Identity
    public Task<Account?> ReadAccount(string accountId) => Call<string, Account?>(TrustOperations.ReadAccount, accountId);

    public Task EnsureAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return Signed(TrustOperations.EnsureAccount, account, account.AccountId);
    }

    public Task<BrainProvision> CreateBrain(string accountId, string principalId, string operationId, string displayName, BrainKind kind)
        => Signed<BrainRequest, BrainProvision>(TrustOperations.CreateBrain, new BrainRequest(accountId, principalId, operationId, displayName, kind), accountId);

    public async Task<BrainProvision> EnsureBrain(Member owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new(owner, await Signed<Member, bool>(TrustOperations.EnsureBrain, owner, owner.AccountId));
    }

    // R63: the provisioning machine binds itself to the account first (first registrant wins, R52), then provisions
    // the owner in a call signed for that account. Both are open to a registration-only link (T20-4).
    public async Task<BrainProvision> ProvisionOwner(Member owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var machine = signer.Machine;
        await RegisterMachine(machine.MachineId, machine.PublicKey, machine.AgreementKey, owner.AccountId, PairingSecretHash, signer.Registration(owner.AccountId, PairingSecretHash));
        return new(owner, await Signed<Member, bool>(TrustOperations.ProvisionOwner, owner, owner.AccountId));
    }

    public Task<bool> IsRegistered(string principalId) => Call<string, bool>(TrustOperations.IsRegistered, principalId);

    public Task<string?> RegisteredBy(string principalId) => Call<string, string?>(TrustOperations.RegisteredBy, principalId);

    public Task<BrainProvision> RegisterPrincipal(string principalId, string displayName)
        => Signed<PrincipalRegistration, BrainProvision>(TrustOperations.RegisterPrincipal, new PrincipalRegistration(principalId, displayName), principalId);

    public Task<BrainProvision?> CompleteRegistration(string principalId)
        => Signed<PrincipalRegistration, BrainProvision?>(TrustOperations.CompleteRegistration, new PrincipalRegistration(principalId, ""), principalId);

    public Task<BrainEntry[]> BrainsOf(string principalId) => Call<string, BrainEntry[]>(TrustOperations.BrainsOf, principalId);

    public Task<BrainKind> KindOf(string brainScopeId) => Call<string, BrainKind>(TrustOperations.KindOf, brainScopeId);

    public Task<bool> BrainExists(string brainScopeId) => Call<string, bool>(TrustOperations.BrainExists, brainScopeId);

    public Task RemoveMember(string brainScopeId, string accountId)
        => Signed(TrustOperations.RemoveMember, new MemberRemoval(brainScopeId, accountId), accountId);

    public Task ContributeMachine(string circleBrainId, string machineId)
        => Signed(TrustOperations.ContributeMachine, new CircleMachine(circleBrainId, machineId), signer.Machine.MachineId);

    public Task WithdrawMachine(string circleBrainId, string machineId)
        => Signed(TrustOperations.WithdrawMachine, new CircleMachine(circleBrainId, machineId), signer.Machine.MachineId);

    public Task<string[]> MachinesOf(string circleBrainId) => Call<string, string[]>(TrustOperations.MachinesOf, circleBrainId);

    public async Task<string[]> AccountsOf(string machineId) => (await Machine(machineId))?.Accounts ?? [];

    public string? MachineId => signer.Machine.MachineId;

    public Task<Member?> Membership(string brainScopeId, string principalId) => Call<(string, string), Member?>(TrustOperations.Membership, (brainScopeId, principalId));

    public Task<Member?> EnsureMember(Member member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return Signed<Member, Member?>(TrustOperations.EnsureMember, member, member.AccountId);
    }

    // R68: the decision is an unsigned read; only an allowed decision resting on a Once grant spends it, signed.
    public async Task<CallDecision?> Authorize(CallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = await Call<CallRequest, CallDecision?>(TrustOperations.Authorize, request);
        return decision is { Allowed: true, ConsumesOnce: true }
            ? await Signed<CallRequest, CallDecision?>(TrustOperations.Consume, request, request.Caller.AccountId)
            : decision;
    }

    public Task<Grant[]> ListGrants(string brainScopeId) => Call<string, Grant[]>(TrustOperations.ListGrants, brainScopeId);

    public Task<Grant> GrantAsOwner(CallerContext caller, Grant grant)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return Signed<OwnerGrant, Grant>(TrustOperations.GrantAsOwner, new OwnerGrant(caller, grant), caller.AccountId);
    }

    public Task RevokeAsOwner(CallerContext caller, string appId, string semanticTypeId, GrantMode mode)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return Signed(TrustOperations.RevokeAsOwner, new OwnerRevoke(caller, appId, semanticTypeId, mode), caller.AccountId);
    }
}
