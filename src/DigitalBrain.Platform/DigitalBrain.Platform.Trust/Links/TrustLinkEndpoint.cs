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
    public const string NodeRegister = "node.register";
    public const string NodeOnline = "node.online";
    public const string NodeAudit = "node.audit";
    public const string NodePlace = "node.place";
    public const string NodeList = "node.list";
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
        _ = link.Closed.ContinueWith(_ => _links.TryRemove(new(link.RemoteMachineId, link)), TaskScheduler.Default);
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
        [TrustOperations.NodeRegister] = Registered(async call =>
        {
            var (nodeId, owner) = call.Arg<(string, string)>();
            call.RequireAccount(owner);
            if (await NodeOwner(nodeId) is { } current) { call.RequireAccount(current); }
            await Directory.Register(nodeId, owner);
            return true;
        }),
        [TrustOperations.NodeOnline] = Registered(async call =>
        {
            var (nodeId, online) = call.Arg<(string, bool)>();
            call.RequireAccount(await NodeOwner(nodeId) ?? throw new KeyNotFoundException($"Node {nodeId} is not registered."));
            await Directory.SetOnline(nodeId, online);
            return true;
        }),
        [TrustOperations.NodeAudit] = Registered(async call =>
        {
            var (nodeId, passed) = call.Arg<(string, bool)>();
            call.RequireAccount(await NodeOwner(nodeId) ?? throw new KeyNotFoundException($"Node {nodeId} is not registered."));
            await Directory.ReportAudit(nodeId, passed);
            return true;
        }),
        [TrustOperations.NodePlace] = Registered(async call =>
        {
            var (count, exclude) = call.Arg<(int, string[])>();
            return await Directory.Place(count, exclude);
        }),
        [TrustOperations.NodeList] = Registered(async _ => await Directory.List()),
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
        [TrustOperations.BrainsOf] = Registered(async call => await grains.GetGrain<IPrincipal>(call.Arg<string>()).Brains()),
        [TrustOperations.Membership] = Registered(async call =>
        {
            var (brainScopeId, principalId) = call.Arg<(string, string)>();
            return await grains.GetGrain<IBrainAuthority>(brainScopeId).Membership(principalId);
        }),
        [TrustOperations.EnsureMember] = Registered(async call => await Writes.EnsureMember(call.Signed<Member>())),
        [TrustOperations.Authorize] = Registered(async call =>
        {
            var request = call.Arg<CallRequest>();
            return await grains.GetGrain<IBrainAuthority>(BrainScope.Create(request.Caller.AccountId, request.Caller.BrainId).Id).Authorize(request);
        }),
        [TrustOperations.Consume] = Registered(async call => await Writes.Consume(call.Signed<CallRequest>())),
        [TrustOperations.ListGrants] = Registered(async call => await grains.GetGrain<IBrainAuthority>(call.Arg<string>()).ListGrants()),
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
