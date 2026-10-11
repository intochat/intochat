using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization;

namespace DigitalBrain.Platform.Links;

// The operations a machine sends over its trust link (T20-7), one per trust-client member.
internal static class TrustOperations
{
    public const string Deployment = "trust.deployment";
    public const string RegisterMachine = "trust.register-machine";
    public const string Machine = "trust.machine";
    public const string Announce = "trust.announce";
    public const string SequencerGet = "sequencer.get";
    public const string CompareAndSet = "sequencer.compare-and-set";
    public const string NodeRegisterSigned = "node.register-signed";
    public const string NodeHeartbeat = "node.heartbeat";
    public const string NodeAudit = "node.audit";
    public const string NodePlace = "node.place";
    public const string NodeList = "node.list";
    public const string CreditsPending = "credits.pending";
    public const string CreditsAcknowledge = "credits.acknowledge";
    public const string ShardPut = "shard.put";
    public const string ShardGet = "shard.get";
    public const string ShardProve = "shard.prove";
    public const string ShardDiscard = "shard.discard";
    public const string KeyGet = "keyring.get";
    public const string KeyAdd = "keyring.try-add";
    public const string OwnerOf = "keyring.owner-of";
    public const string MarketList = "marketplace.list";
    public const string MarketRead = "marketplace.read";
    public const string MarketUpsert = "marketplace.upsert";
    public const string MarketRemove = "marketplace.remove";
    public const string ReadAccount = "identity.read-account";
    public const string EnsureAccount = "identity.ensure-account";
    public const string CreateBrain = "identity.create-brain";
    public const string EnsureBrain = "identity.ensure-brain";
    public const string ProvisionOwner = "identity.provision-owner";
    public const string IsRegistered = "identity.is-registered";
    public const string RegisteredBy = "identity.registered-by";
    public const string RegisterPrincipal = "identity.register-principal";
    public const string CompleteRegistration = "identity.complete-registration";
    public const string BrainsOf = "identity.brains-of";
    public const string Membership = "identity.membership";
    public const string EnsureMember = "identity.ensure-member";
    public const string Authorize = "identity.authorize";
    public const string Consume = "identity.consume";
    public const string ListGrants = "identity.list-grants";
    public const string GrantAsOwner = "identity.grant-as-owner";
    public const string RevokeAsOwner = "identity.revoke-as-owner";
    public const string RelayPort = "relay.port";
    public const string RelayAllocate = "relay.allocate";
    public const string RelayRendezvous = "relay.rendezvous";
}

// The trust host's link endpoint (T20-7): a QUIC listener machines dial instead of an Orleans gateway. Each request
// runs on the trust silo's own grains for the link's authenticated machine id, after the checks the link makes
// possible (T20-4, T20-8): an unregistered machine may only register (registration-only link, R90); every other
// call needs a registered machine; account-scoped writes need the account among the machine's bindings (R55); a
// signed payload must be signed by the link's own machine. The relay and rendezvous run beside it (T20-5, R84).
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class TrustLinkEndpoint(IGrainFactory grains, IOptions<TrustOptions> trust, IOptions<LinkOptions> links, Serializer serializer,
    TimeProvider time, ILogger<TrustLinkEndpoint> logger) : ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    public const string SourceName = "DigitalBrain.Trust.Link";

    public static readonly ActivitySource Source = new(SourceName);

    private readonly ConcurrentDictionary<string, QuicMachineLink> _links = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _newAccounts = [];
    private QuicLinkListener? _listener;
    private Relay? _relay;
    private Rendezvous? _rendezvous;
    private FileMachineSigningKey? _key;
    private LinkCertificate? _certificate;
    private Dictionary<string, Operation>? _operations;

    public IPEndPoint LinkEndPoint => _listener?.LocalEndPoint ?? throw new InvalidOperationException("The trust link endpoint has not started.");

    public IPEndPoint RelayEndPoint => _relay?.LocalEndPoint ?? throw new InvalidOperationException("The trust relay has not started.");

    public string TrustId => _key is { } key ? MachineKeys.IdOf(key.PublicKey) : throw new InvalidOperationException("The trust link endpoint has not started.");

    internal Relay Relay => _relay ?? throw new InvalidOperationException("The trust relay has not started.");

    // Facts only: whether a machine's registration is remembered, and the link it is served on.
    internal bool Remembers(string machineId) => _registered.ContainsKey(machineId);

    internal QuicMachineLink? LinkOf(string machineId) => _links.GetValueOrDefault(machineId);

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(nameof(TrustLinkEndpoint), ServiceLifecycleStage.Active, Start, Stop);

    public async Task Start(CancellationToken cancellationToken)
    {
        var options = trust.Value;
        _key = FileMachineSigningKey.LoadOrCreate(options.IdentityPath);
        var deployment = await grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key).DeploymentId().ConfigureAwait(false);
        var address = options.AdvertisedAddress is { } advertised ? IPAddress.Parse(advertised) : IPAddress.Any;
        _relay = Relay.Start(new IPEndPoint(address, options.RelayPort), time);
        _rendezvous = new Rendezvous(id => _links.GetValueOrDefault(id), _relay, _key, serializer);
        _operations = Operations();
        // The trust link admits any machine whose endorsement holds, for any deployment it was made for (R90):
        // registration is checked per call, and a machine learns the deployment id over this very link.
        _certificate = new LinkCertificate(_key, deployment, time);
        var end = new LinkEnd(() => _certificate.Current, null, _ => true, links.Value, serializer, Dispatch, time, Admit);
        _listener = await QuicLinkListener.Start(new IPEndPoint(address, options.LinkPort), end, Accepted, cancellationToken).ConfigureAwait(false);
        Log.Listening(logger, _listener.LocalEndPoint, _relay.LocalEndPoint, TrustId, deployment);
    }

    private void Accepted(QuicMachineLink link)
    {
        _links.AddOrUpdate(link.RemoteMachineId, link, (_, old) => { old.DisposeAsync().AsTask().Ignore(); return link; });
        Log.Linked(logger, link.RemoteMachineId, link.RemoteEndPoint);
        // R99: what this host remembers of a link's registration goes with the link (runs before later awaiters).
        _ = link.Closed.ContinueWith(closed =>
        {
            if (_links.TryRemove(new(link.RemoteMachineId, link))) { _registered.TryRemove(link.RemoteMachineId, out _); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<LinkReply> Dispatch(QuicMachineLink link, LinkRequest request, CancellationToken cancellationToken)
    {
        using var activity = Source.StartActivity($"{nameof(TrustLinkEndpoint)} {request.Operation}", ActivityKind.Server, request.TraceParent);
        activity?.SetTag("digitalbrain.machine", link.RemoteMachineId);
        if (_operations?.GetValueOrDefault(request.Operation) is not { } operation)
        { throw new InvalidOperationException($"The trust link has no operation '{request.Operation}'."); }
        var machine = await Registry(link.RemoteMachineId).Read().ConfigureAwait(false);
        if (machine is null && !operation.Unregistered)
        {
            Log.Refused(logger, link.RemoteMachineId, request.Operation);
            throw new UnauthorizedAccessException($"Machine {link.RemoteMachineId} is not registered; its link may only register (T20-4).");
        }
        try { return new LinkReply(await operation.Run(new Call(link, machine, request.Payload, serializer, cancellationToken)).ConfigureAwait(false), null, null); }
        catch (UnauthorizedAccessException)
        {
            Log.Refused(logger, link.RemoteMachineId, request.Operation);
            throw;
        }
    }

    // An unregistered machine's link reads only registration-sized frames, two at a time (fix round 1, T20-4).
    private async ValueTask<LinkAdmission> Admit(QuicMachineLink link)
    {
        if (_registered.ContainsKey(link.RemoteMachineId)) { return LinkAdmission.Full; }
        if (await Registry(link.RemoteMachineId).Read().ConfigureAwait(false) is null) { return LinkAdmission.Registration; }
        _registered[link.RemoteMachineId] = true;
        return LinkAdmission.Full;
    }

    private IMachineRegistry Registry(string machineId) => grains.GetGrain<IMachineRegistry>(machineId);

    private IStorageNodeDirectory Directory => grains.GetGrain<IStorageNodeDirectory>(IStorageNodeDirectory.Key);

    private IStorageShard Shard(string nodeId, string cid) => grains.GetGrain<IStorageShard>(StorageShards.Key(nodeId, cid));

    private IIdentityWrites Writes => grains.GetGrain<IIdentityWrites>(IIdentityWrites.Key);

    private IMarketplaceCatalog Catalog => grains.GetGrain<IMarketplaceCatalog>(IMarketplaceCatalog.Key);

    private Dictionary<string, Operation> Operations() => new(StringComparer.Ordinal)
    {
        [TrustOperations.Deployment] = Open(async _ => await Directory.DeploymentId()),
        [TrustOperations.RegisterMachine] = Open(async call =>
        {
            var (machineId, publicKey, account, pairing, signature) = call.Arg<(string, byte[], string, byte[], byte[])>();
            // A link registers only its own key: the one that endorsed its certificate.
            if (machineId != call.Link.RemoteMachineId || !publicKey.AsSpan().SequenceEqual(call.Link.Peer.PublicKey))
            { throw new UnauthorizedAccessException($"Machine {call.Link.RemoteMachineId} may register only itself."); }
            if ((await grains.GetGrain<IAccountMachines>(account).Machines()).Length == 0) { CountNewAccount(call.Link.RemoteEndPoint.Address, account); }
            await Registry(machineId).Register(publicKey, account, pairing, signature);
            return true;
        }),
        [TrustOperations.ProvisionOwner] = Open(async call => await Writes.ProvisionOwner(call.Signed<Member>())),
        [TrustOperations.Machine] = Registered(async call => await Registry(call.Arg<string>()).Read()),
        [TrustOperations.Announce] = Registered(async call =>
        {
            var signed = call.Signed<MachineCandidates>();
            await Registry(signed.MachineId).Announce(signed.Call(), signed.Payload);
            return true;
        }),
        [TrustOperations.SequencerGet] = Registered(async call => await grains.GetGrain<ISequencerGrain>(call.Arg<string>()).Get()),
        [TrustOperations.CompareAndSet] = Registered(async call =>
        {
            var (hash, expected, next) = call.Arg<(string, long, HeadRecord)>();
            return await grains.GetGrain<ISequencerGrain>(hash).CompareAndSet(expected, next);
        }),
        // T23-5: signed by this link's machine (Signed), for its own node and an account it serves, never `platform`.
        [TrustOperations.NodeRegisterSigned] = Registered(async call => { await Directory.RegisterNode(call.Signed<NodeRegistration>()); return true; }),
        [TrustOperations.NodeHeartbeat] = Registered(async call =>
        {
            var (nodeId, usedBytes) = call.Arg<(string, long)>();
            await Directory.Heartbeat(call.Link.RemoteMachineId, nodeId, usedBytes);
            return true;
        }),
        // T23-10/R112: the auditor reports, bound to its placements; the node's owner never does.
        [TrustOperations.NodeAudit] = Registered(async call => await Directory.Report(call.Arg<AuditReport>(), call.Machine!.Accounts)),
        // T23-6: never onto a node of an account this machine serves.
        [TrustOperations.NodePlace] = Registered(async call =>
        {
            var (count, exclude, shardBytes) = call.Arg<(int, string[], int)>();
            return await Directory.Place(count, exclude, shardBytes, call.Machine!.Accounts);
        }),
        [TrustOperations.NodeList] = Registered(async _ => await Directory.List()),
        // T23-11/R55: an account's storage credits are read and acknowledged only by a machine bound to it.
        [TrustOperations.CreditsPending] = Registered(async call =>
        {
            var account = call.Arg<string>();
            call.RequireAccount(account);
            return await grains.GetGrain<IStorageCredits>(account).Pending();
        }),
        [TrustOperations.CreditsAcknowledge] = Registered(async call =>
        {
            var (account, entryIds) = call.Arg<(string, string[])>();
            call.RequireAccount(account);
            await grains.GetGrain<IStorageCredits>(account).Acknowledge(entryIds);
            return true;
        }),
        [TrustOperations.ShardPut] = Registered(async call =>
        {
            var (nodeId, cid, sealedShard) = call.Arg<(string, string, byte[])>();
            // The platform's seed nodes store for every machine; any other node only for its owner's machines.
            var owner = await NodeOwner(nodeId) ?? throw new KeyNotFoundException($"Node {nodeId} is not registered.");
            if (owner != SeedNodeRegistration.Owner) { call.RequireAccount(owner); }
            await Shard(nodeId, cid).Put(sealedShard, call.Link.RemoteMachineId);
            return true;
        }),
        [TrustOperations.ShardGet] = Registered(async call =>
        {
            var (nodeId, cid) = call.Arg<(string, string)>();
            return await Shard(nodeId, cid).Get();
        }),
        [TrustOperations.ShardProve] = Registered(async call =>
        {
            var (nodeId, cid, nonce) = call.Arg<(string, string, byte[])>();
            return await Shard(nodeId, cid).Prove(nonce);
        }),
        [TrustOperations.ShardDiscard] = Registered(async call =>
        {
            var (nodeId, cid) = call.Arg<(string, string)>();
            var shard = Shard(nodeId, cid);
            // A machine drops the claims of its own account's machines only, never another account's.
            var putters = await shard.Putters();
            var ours = new List<string>();
            foreach (var putter in putters)
            {
                if (putter == call.Link.RemoteMachineId || ((await Registry(putter).Read())?.Accounts ?? []).Any(call.Machine!.Accounts.Contains))
                { ours.Add(putter); }
            }
            if (putters.Length > 0 && ours.Count == 0)
            { throw new UnauthorizedAccessException($"Machine {call.Link.RemoteMachineId} serves no account of a machine that put shard {cid}."); }
            await shard.Forget([.. ours]);
            return true;
        }),
        [TrustOperations.KeyGet] = Registered(async call =>
        {
            var (brainId, partId) = call.Arg<(string, string)>();
            await RequireBrain(call, brainId);
            return await grains.GetGrain<IKeyringGrain>(brainId).Get(partId);
        }),
        [TrustOperations.KeyAdd] = Registered(async call =>
        {
            var (brainId, partId, wrapped) = call.Arg<(string, string, byte[])>();
            call.RequireAccount(await grains.GetGrain<IBrainAuthority>(brainId).Account()
                ?? throw new UnauthorizedAccessException($"Brain {brainId} has no owner on record; its keyring takes no key."));
            return await grains.GetGrain<IKeyringGrain>(brainId).TryAdd(partId, wrapped);
        }),
        [TrustOperations.OwnerOf] = Registered(async call => await grains.GetGrain<IBrainAuthority>(call.Arg<string>()).Account()),
        [TrustOperations.MarketList] = Registered(async _ => await Catalog.List()),
        [TrustOperations.MarketRead] = Registered(async call =>
        {
            var (publisher, name) = call.Arg<(string, string)>();
            return await Catalog.Read(publisher, name);
        }),
        [TrustOperations.MarketUpsert] = Registered(async call => { await Catalog.Upsert(call.Signed<MarketplaceRecord>()); return true; }),
        [TrustOperations.MarketRemove] = Registered(async call => { await Catalog.Remove(call.Signed<MarketplaceRemoval>()); return true; }),
        [TrustOperations.ReadAccount] = Registered(async call => await grains.GetGrain<IAccount>(call.Arg<string>()).Read()),
        [TrustOperations.EnsureAccount] = Registered(async call => { await Writes.EnsureAccount(call.Signed<Account>()); return true; }),
        [TrustOperations.CreateBrain] = Registered(async call => await Writes.CreateBrain(call.Signed<BrainRequest>())),
        [TrustOperations.EnsureBrain] = Registered(async call => await Writes.EnsureBrain(call.Signed<Member>())),
        [TrustOperations.IsRegistered] = Registered(async call => await grains.GetGrain<IPrincipal>(call.Arg<string>()).IsRegistered()),
        [TrustOperations.RegisteredBy] = Registered(async call => await grains.GetGrain<IPrincipal>(call.Arg<string>()).RegisteredBy()),
        [TrustOperations.RegisterPrincipal] = Registered(async call => await Writes.RegisterPrincipal(call.Signed<PrincipalRegistration>())),
        [TrustOperations.CompleteRegistration] = Registered(async call => await Writes.CompleteRegistration(call.Signed<PrincipalRegistration>())),
        [TrustOperations.BrainsOf] = Registered(async call =>
        {
            var principalId = call.Arg<string>();
            var principal = grains.GetGrain<IPrincipal>(principalId);
            // A principal's brains are read by its own account's machines or the machine that registered it (R67).
            if (!call.Machine!.Accounts.Contains(principalId, StringComparer.Ordinal) && await principal.RegisteredBy() != call.Link.RemoteMachineId)
            { throw new UnauthorizedAccessException($"Machine {call.Link.RemoteMachineId} is not bound to principal {principalId}."); }
            return await principal.Brains();
        }),
        [TrustOperations.Membership] = Registered(async call =>
        {
            var (brainScopeId, principalId) = call.Arg<(string, string)>();
            await RequireBrain(call, brainScopeId);
            return await grains.GetGrain<IBrainAuthority>(brainScopeId).Membership(principalId);
        }),
        [TrustOperations.EnsureMember] = Registered(async call => await Writes.EnsureMember(call.Signed<Member>())),
        [TrustOperations.Authorize] = Registered(async call =>
        {
            var request = call.Arg<CallRequest>();
            return await grains.GetGrain<IBrainAuthority>(BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id).Authorize(request);
        }),
        [TrustOperations.Consume] = Registered(async call => await Writes.Consume(call.Signed<CallRequest>())),
        [TrustOperations.ListGrants] = Registered(async call =>
        {
            var brainScopeId = call.Arg<string>();
            await RequireBrain(call, brainScopeId);
            return await grains.GetGrain<IBrainAuthority>(brainScopeId).ListGrants();
        }),
        [TrustOperations.GrantAsOwner] = Registered(async call => await Writes.GrantAsOwner(call.Signed<OwnerGrant>())),
        [TrustOperations.RevokeAsOwner] = Registered(async call => { await Writes.RevokeAsOwner(call.Signed<OwnerRevoke>()); return true; }),
        [TrustOperations.RelayPort] = Registered(_ => Task.FromResult(Relay.LocalEndPoint.Port)),
        [TrustOperations.RelayAllocate] = Registered(async call =>
        {
            var peer = call.Arg<string>();
            if (await Registry(peer).Read() is null) { throw new UnauthorizedAccessException($"Machine {peer} is not registered."); }
            return await _rendezvous!.Slot(call.Link.RemoteMachineId, peer, call.CancellationToken);
        }),
        [TrustOperations.RelayRendezvous] = Registered(async call =>
        {
            var (peer, reflected, candidates) = call.Arg<(string, IPEndPoint, IPEndPoint[])>();
            if (await Registry(peer).Read() is null) { throw new UnauthorizedAccessException($"Machine {peer} is not registered."); }
            return await _rendezvous!.Offer(call.Link.RemoteMachineId, peer, reflected, candidates, call.CancellationToken);
        }),
    };

    // R95/R98: a brain's wrapped keys, members and grants are read only by a machine bound to the brain's owner
    // account, or to an account that is a member of that brain.
    private async Task RequireBrain(Call call, string brainScopeId)
    {
        var authority = grains.GetGrain<IBrainAuthority>(brainScopeId);
        var owner = await authority.Account() ?? throw new UnauthorizedAccessException($"Brain {brainScopeId} has no owner on record.");
        var accounts = call.Machine?.Accounts ?? [];
        if (accounts.Contains(owner, StringComparer.Ordinal)) { return; }
        foreach (var account in accounts)
        {
            if (await authority.Membership(account) is not null) { return; }
        }
        throw new UnauthorizedAccessException($"Machine {call.Link.RemoteMachineId} serves no owner or member account of brain {brainScopeId}.");
    }

    // R99: one source address binds at most RegistrationsPerAddressPerMinute new accounts a minute.
    private void CountNewAccount(IPAddress address, string account)
    {
        var now = time.GetUtcNow();
        lock (_newAccounts)
        {
            if (!_newAccounts.TryGetValue(address, out var recent)) { _newAccounts[address] = recent = new Queue<DateTimeOffset>(); }
            while (recent.Count > 0 && now - recent.Peek() >= TimeSpan.FromMinutes(1)) { recent.Dequeue(); }
            if (recent.Count >= links.Value.RegistrationsPerAddressPerMinute)
            { throw new UnauthorizedAccessException($"Address {address} registered a new account within the last minute; account {account} is refused for now."); }
            recent.Enqueue(now);
        }
    }

    private async Task<string?> NodeOwner(string nodeId)
        => (await Directory.List()).FirstOrDefault(node => node.NodeId == nodeId)?.OwnerAccount;

    private Operation Open<T>(Func<Call, Task<T>> run) => new(true, async call => serializer.SerializeToArray(await run(call)));

    private Operation Registered<T>(Func<Call, Task<T>> run) => new(false, async call => serializer.SerializeToArray(await run(call)));

    private sealed record Operation(bool Unregistered, Func<Call, Task<byte[]>> Run);

    private sealed class Call(QuicMachineLink link, RegisteredMachine? machine, byte[] payload, Serializer serializer, CancellationToken cancellationToken)
    {
        public QuicMachineLink Link { get; } = link;

        public RegisteredMachine? Machine { get; } = machine;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public T Arg<T>() => serializer.Deserialize<T>(payload)!;

        // The link proves the machine; the signature keeps the account claim bound to the payload (T20-8).
        public MachineSigned<T> Signed<T>()
        {
            var signed = Arg<MachineSigned<T>>();
            if (signed.MachineId != Link.RemoteMachineId)
            { throw new UnauthorizedAccessException($"A payload signed by machine {signed.MachineId} arrived on machine {Link.RemoteMachineId}'s link."); }
            return signed;
        }

        public void RequireAccount(string account)
        {
            if (Machine is null || !Machine.Accounts.Contains(account, StringComparer.Ordinal))
            { throw new UnauthorizedAccessException($"Machine {Link.RemoteMachineId} does not serve account {account}."); }
        }
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        if (_listener is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            await listener.CloseLinks().ConfigureAwait(false);
        }
        if (_relay is { } relay) { await relay.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        await Stop(CancellationToken.None).ConfigureAwait(false);
        _certificate?.Dispose();
        _key?.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Trust link endpoint on {Link}, relay on {Relay}, trust id {TrustId}, deployment {Deployment}.")]
        public static partial void Listening(ILogger logger, IPEndPoint link, IPEndPoint relay, string trustId, string deployment);

        [LoggerMessage(Level = LogLevel.Information, Message = "Machine {MachineId} linked from {Endpoint}.")]
        public static partial void Linked(ILogger logger, string machineId, IPEndPoint endpoint);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Machine {MachineId} was refused {Operation} on its trust link.")]
        public static partial void Refused(ILogger logger, string machineId, string operation);
    }
}
