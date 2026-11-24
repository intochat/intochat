using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using Grpc.Core;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Kernel.Platform.Proto;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Keys;
using IntoChat.OS.Modules.Identity.Keys;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests;
using IntoChat.OS.Tests.Integration.Journeys;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static IntoChat.OS.Modules.Apps.Tests.Integration.Files.FileSharesDoorFacts;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Files;

// A brain's key pair lives in the identity module, sealed (IBrainKeys), and the friend roster keeps no private key. A roster
// written by an earlier version still holds the pair: it hands the same pair over on its next use and stores itself without
// it. These facts run on a silo with the identity module's own sealer (BrainSecretSealer over the brain store's keys) and
// read what is stored from the brain's journal, not from the neurons.
public sealed class BrainKeysMoveFacts
{
    private const string Kim = "kim";
    private const string Lee = "lee";
    private const string Nat = "nat";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Pair(byte[] Signing, byte[] Exchange, BrainPublicKeys Public)
    {
        public static Pair New()
        {
            using var generated = BrainKeyPair.Generate();
            return new(generated.SigningKey.ExportPkcs8PrivateKey(), generated.ExchangeKey.ExportPkcs8PrivateKey(), generated.ExportPublicKeys());
        }
    }

    // The roster as an earlier version stored it: the private keys in the record.
    private static FriendsRecord EarlierRoster(Pair pair, params Friend[] friends)
        => new() { Friends = friends, SigningKeyPkcs8 = pair.Signing, ExchangeKeyPkcs8 = pair.Exchange };

    [Fact]
    public async Task ARosterOfAnEarlierVersionHandsTheSamePairToIdentityAndKeepsNoPrivateKey()
    {
        await using var world = await World.Start();
        var pair = Pair.New();
        await Seed(world, Kim, EarlierRoster(pair));
        Assert.Contains(Convert.ToBase64String(pair.Signing), await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);

        BrainPublicKeys first, second;
        using (As(Kim))
        {
            first = await world.Friends(Kim).PublicKeys(Ct);
            second = await world.Friends(Kim).PublicKeys(Ct);
        }

        // The same pair, not a new one: the public halves are byte for byte what the roster's keys gave.
        Assert.Equal(pair.Public.SigningKey, first.SigningKey);
        Assert.Equal(pair.Public.ExchangeKey, first.ExchangeKey);
        Assert.Equal(first.SigningKey, second.SigningKey);
        Assert.Equal(first.ExchangeKey, second.ExchangeKey);

        // The roster record keeps its other fields and no key.
        var roster = await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
        Assert.Contains("\"signingKeyPkcs8\":null", roster, StringComparison.Ordinal);
        Assert.Contains("\"exchangeKeyPkcs8\":null", roster, StringComparison.Ordinal);
        AssertNoPrivateKey(roster, pair);

        // Identity holds it sealed: the stored record is ciphertext, and it opens, under this brain's key, to the very keys
        // the roster held.
        var stored = await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart);
        AssertNoPrivateKey(stored, pair);
        var (signing, exchange) = await Open(world, Kim, stored);
        Assert.Equal(pair.Signing, signing);
        Assert.Equal(pair.Exchange, exchange);
    }

    // Interrupted after identity adopted the pair and before the roster was stored without it: both hold the same pair. The
    // next call finds that, changes nothing in identity and finishes the roster.
    [Fact]
    public async Task AMoveInterruptedBetweenItsTwoStepsIsFinishedByTheNextCall()
    {
        await using var world = await World.Start();
        var pair = Pair.New();
        await Seed(world, Kim, EarlierRoster(pair));
        using (As(Kim)) { await world.Friends(Kim).PublicKeys(Ct); }
        var kept = await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart);

        // The state a crash between the two steps leaves: put the keys back into the roster record while it sleeps.
        await Rewrite(world, Kim, roster => { roster.SigningKeyPkcs8 = pair.Signing; roster.ExchangeKeyPkcs8 = pair.Exchange; });
        Assert.Contains(Convert.ToBase64String(pair.Signing), await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);

        BrainPublicKeys again;
        using (As(Kim)) { again = await world.Friends(Kim).PublicKeys(Ct); }

        Assert.Equal(pair.Public.SigningKey, again.SigningKey);
        Assert.Equal(pair.Public.ExchangeKey, again.ExchangeKey);
        AssertNoPrivateKey(await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), pair);
        // Adopting a pair it already holds wrote nothing: the stored record is the one from before, seal and all.
        Assert.Equal(kept, await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart));
    }

    // Identity already holds a pair and the roster record holds another: identity's is the brain's. Nothing is overwritten
    // and the roster record is left as it is.
    [Fact]
    public async Task APairThatIsNotTheBrainsIsNeitherAdoptedNorErased()
    {
        await using var world = await World.Start();
        var pair = Pair.New();
        var other = Pair.New();
        await Seed(world, Kim, EarlierRoster(pair));
        using (As(Kim)) { await world.Friends(Kim).PublicKeys(Ct); }
        var kept = await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart);

        await Rewrite(world, Kim, roster => { roster.SigningKeyPkcs8 = other.Signing; roster.ExchangeKeyPkcs8 = other.Exchange; });
        BrainPublicKeys answered;
        using (As(Kim)) { answered = await world.Friends(Kim).PublicKeys(Ct); }

        Assert.Equal(pair.Public.SigningKey, answered.SigningKey);
        Assert.Equal(kept, await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart));
        Assert.Contains(Convert.ToBase64String(other.Signing), await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrainMadeAfterTheMoveNeverWritesAKeyToItsRoster()
    {
        await using var world = await World.Start();

        Code code;
        BrainPublicKeys minted, later;
        using (As(Kim))
        {
            code = await world.Friends(Kim).Mint();
            minted = await world.Friends(Kim).PublicKeys(Ct);
        }

        Friend kimAsLeeSeesHer;
        using (As(Lee)) { kimAsLeeSeesHer = await world.Friends(Lee).Redeem(code); }
        await world.Runtime.Deactivate(FriendsDefaults.Id(Kim));
        await world.Runtime.Deactivate(IBrainKeys.Id(new BrainId(Kim)));
        using (As(Kim)) { later = await world.Friends(Kim).PublicKeys(Ct); }

        // One pair, made once, in identity; the handshake carried its public halves.
        Assert.Equal(minted.SigningKey, later.SigningKey);
        Assert.Equal(minted.ExchangeKey, later.ExchangeKey);
        Assert.Equal(minted.SigningKey, kimAsLeeSeesHer.PublicKeys!.SigningKey);
        foreach (var brain in (string[])[Kim, Lee])
        {
            var roster = await Stored(world, brain, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
            Assert.Contains("\"signingKeyPkcs8\":null", roster, StringComparison.Ordinal);
            Assert.Contains("\"exchangeKeyPkcs8\":null", roster, StringComparison.Ordinal);
            var (signing, _) = await Open(world, brain, await Stored(world, brain, IBrainKeys.Type, IBrainKeys.KeyPart));
            Assert.DoesNotContain(Convert.ToBase64String(signing), roster, StringComparison.Ordinal);
        }
    }

    // Kim and Lee became friends on an earlier version: each roster holds its own pair and the other's public keys. Kim's
    // brain moves its keys; Lee's has not (its record still holds them). What Lee holds of Kim still verifies what Kim signs,
    // the two still reach each other, and Kim still makes friends with a brain whose roster has not moved either.
    [Fact]
    public async Task FriendshipsFormedBeforeTheMoveStillVerifyAndStillExchangeWithABrainThatHasNotMoved()
    {
        await using var world = await World.Start();
        var (kim, lee, nat) = (Pair.New(), Pair.New(), Pair.New());
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        await Seed(world, Kim, EarlierRoster(kim, new Friend(new AccountId(Lee), new BrainId(Lee), "Lee", since, lee.Public)));
        await Seed(world, Lee, EarlierRoster(lee, new Friend(new AccountId(Kim), new BrainId(Kim), "Kim", since, kim.Public)));
        await Seed(world, Nat, EarlierRoster(nat));

        BrainPublicKeys kimNow;
        using (As(Kim)) { kimNow = await world.Friends(Kim).PublicKeys(Ct); }
        AssertNoPrivateKey(await Stored(world, Kim, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), kim);

        // Lee's side is untouched: the roster of the earlier version, keys and all, listing Kim with the keys it got then.
        Friend kimAsLeeHoldsHer;
        using (As(Lee)) { kimAsLeeHoldsHer = Assert.Single(await world.Friends(Lee).List()); }
        Assert.Contains(Convert.ToBase64String(lee.Signing), await Stored(world, Lee, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);
        Assert.Equal("", await Stored(world, Lee, IBrainKeys.Type, IBrainKeys.KeyPart));
        Assert.Equal(kimNow.SigningKey, kimAsLeeHoldsHer.PublicKeys!.SigningKey);
        Assert.Equal(kimNow.ExchangeKey, kimAsLeeHoldsHer.PublicKeys.ExchangeKey);

        // Verify: what Kim's brain signs with the key it now holds sealed checks out under the key Lee has held all along.
        var (kimSigning, kimExchange) = await Open(world, Kim, await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart));
        var message = Encoding.UTF8.GetBytes("hello Lee");
        using (var signer = ECDsa.Create())
        using (var verifier = ECDsa.Create())
        {
            signer.ImportPkcs8PrivateKey(kimSigning, out _);
            verifier.ImportSubjectPublicKeyInfo(kimAsLeeHoldsHer.PublicKeys.SigningKey, out _);
            Assert.True(verifier.VerifyData(message, signer.SignData(message, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        }

        // Exchange: the secret Kim derives with Lee's public key is the one Lee derives with the key it holds of Kim.
        using (var kimSide = ECDiffieHellman.Create())
        using (var leeSide = ECDiffieHellman.Create())
        using (var leePublic = ECDiffieHellman.Create())
        using (var kimPublic = ECDiffieHellman.Create())
        {
            kimSide.ImportPkcs8PrivateKey(kimExchange, out _);
            leeSide.ImportPkcs8PrivateKey(lee.Exchange, out _);
            leePublic.ImportSubjectPublicKeyInfo(lee.Public.ExchangeKey, out _);
            kimPublic.ImportSubjectPublicKeyInfo(kimAsLeeHoldsHer.PublicKeys.ExchangeKey, out _);
            Assert.Equal(
                kimSide.DeriveKeyFromHash(leePublic.PublicKey, HashAlgorithmName.SHA256),
                leeSide.DeriveKeyFromHash(kimPublic.PublicKey, HashAlgorithmName.SHA256));
        }

        // The two brains still reach each other through the friendship, with Lee's roster still as it was.
        using (As(Lee)) { Assert.True(await world.Friends(Kim).IsFriend(new AccountId(Lee))); }
        using (As(Kim)) { Assert.True(await world.Friends(Lee).IsFriend(new AccountId(Kim))); }
        Assert.Contains(Convert.ToBase64String(lee.Signing), await Stored(world, Lee, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), StringComparison.Ordinal);

        // A new handshake across the same difference: Nat's roster had not moved when Kim redeemed Nat's code. Each side gets
        // the other's public keys, and Nat's are those of the pair its roster held.
        Code code;
        using (As(Nat)) { code = await world.Friends(Nat).Mint(); }
        Friend natAsKimSeesHim;
        using (As(Kim)) { natAsKimSeesHim = await world.Friends(Kim).Redeem(code); }
        Friend kimAsNatSeesHer;
        using (As(Nat)) { kimAsNatSeesHer = Assert.Single(await world.Friends(Nat).List()); }
        Assert.Equal(nat.Public.SigningKey, natAsKimSeesHim.PublicKeys!.SigningKey);
        Assert.Equal(nat.Public.ExchangeKey, natAsKimSeesHim.PublicKeys.ExchangeKey);
        Assert.Equal(kimNow.SigningKey, kimAsNatSeesHer.PublicKeys!.SigningKey);
        Assert.Equal(kimNow.ExchangeKey, kimAsNatSeesHer.PublicKeys.ExchangeKey);
    }

    // The gRPC channel a signed-in client uses returns the whole state of a neuron it can name (NeuronsGrpcService.ReadState). The product's
    // service is called here as the server calls it, with a call context that carries the request and its caller, over the
    // in-memory runtime: on the in-process silo the kernel's state read does not get as far as any state (it cannot pick a
    // host grain for INeuronHost), so that runtime cannot show what the channel returns. Only the HTTP/2 transport is left out.
    [Fact]
    public async Task ReadStateOverTheNeuronChannelReturnsNoPrivateKey()
    {
        var pair = Pair.New();
        DigitalBrain.Testing.TestOS? os = null;
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(_ => os!.Edge);
        services.AddSingleton(_ => os!.Callers);
        services.AddSingleton(_ => os!.Proxies);
        services.AddSingleton<IBrainSecretSealer, IntoChat.OS.Tests.Support.Fakes.InMemoryBrainSecretSealer>();
        services.AddSingleton<IBrainAccess, OwnBrainOnly>();
        await using var root = services.BuildServiceProvider();
        await using var host = os = DigitalBrain.Testing.TestOS.Local(options => options.Services = root);
        os.Register(typeof(AppsModule).Assembly);
        os.Register(typeof(IntoChat.OS.Modules.Identity.IdentityModule).Assembly);
        var kim = new BrainId(Kim);
        var roster = EarlierRoster(pair, new Friend(new AccountId(Lee), new BrainId(Lee), "Lee", DateTimeOffset.UtcNow, Pair.New().Public));
        os.SeedStoredState(FriendsDefaults.Id(Kim), JsonSerializer.Serialize(roster, FriendsJson.Default.FriendsRecord));

        var observer = new ContractTable(os, new CompiledTypes([typeof(IFriends).Assembly, typeof(IBrainKeys).Assembly]).Entries);
        var surface = new OsNeuronSurface(os.Proxies, observer, new NeuronTypeSearch(observer, [], embeddings: null, callers: os.Callers), plain: root);
        var channel = new NeuronsGrpcService(surface, observer, new ChannelLog(), root);
        RequestCall Request() => new(new DefaultHttpContext { RequestServices = root });
        static NeuronStateRequest StateOf(Type contract, string key) => new() { BrainId = Kim, Contract = contract.FullName, Key = $"{Kim}/{key}" };
        using var person = os.Edge.Enter(os.Edge.Person(Kim, Kim, kim));

        // The roster is awake (a read that needs no key): the in-memory runtime shows the state of an active neuron only.
        Assert.True(await os.Get<IFriends>(FriendsDefaults.Id(Kim)).IsFriend(new AccountId(Lee)));

        // Before the roster's next use the record still holds the keys and the channel returns them: the exposure that is closed.
        var before = (await channel.ReadState(StateOf(typeof(IFriends), FriendsDefaults.KeyPart), Request())).StateJson.ToStringUtf8();
        Assert.Contains(Convert.ToBase64String(pair.Signing), before, StringComparison.Ordinal);

        var keys = await os.Get<IFriends>(FriendsDefaults.Id(Kim)).PublicKeys(Ct);
        Assert.Equal(pair.Public.SigningKey, keys.SigningKey);

        var after = (await channel.ReadState(StateOf(typeof(IFriends), FriendsDefaults.KeyPart), Request())).StateJson.ToStringUtf8();
        Assert.Contains("\"friends\":[", after, StringComparison.Ordinal);
        Assert.Contains("\"signingKeyPkcs8\":null", after, StringComparison.Ordinal);
        Assert.Contains("\"exchangeKeyPkcs8\":null", after, StringComparison.Ordinal);
        AssertNoPrivateKey(after, pair);

        // The key store cannot be named through the channel at all: it is an [Internal] contract, and the kernel's contract
        // table (CompiledTypes) lists none of those, so neither its state nor a call to it is served. The same table is what
        // neurons_list and neurons_call give an agent.
        Assert.DoesNotContain(observer.Listed, static type => type.Name.Contains("BrainKeys", StringComparison.Ordinal) || type.Alias == IBrainKeys.Type);
        foreach (var name in (string[])[typeof(IBrainKeys).FullName!, IBrainKeys.Type])
        {
            var noState = await Assert.ThrowsAsync<RpcException>(() => channel.ReadState(new NeuronStateRequest { BrainId = Kim, Contract = name, Key = $"{Kim}/{IBrainKeys.KeyPart}" }, Request()));
            Assert.Equal(StatusCode.InvalidArgument, noState.StatusCode);
            Assert.Contains("is not a neuron type of this brain", noState.Status.Detail, StringComparison.Ordinal);
            var noCall = await Assert.ThrowsAsync<RpcException>(() => channel.Call(
                new CallRequest { BrainId = Kim, Contract = name, Key = $"{Kim}/{IBrainKeys.KeyPart}", Method = nameof(IBrainKeys.PublicKeys) }, Request()));
            Assert.Equal(StatusCode.InvalidArgument, noCall.StatusCode);
        }

        // What it holds, read from the runtime itself as the kernel can: ciphertext and the public halves.
        var store = (await ((IBrainObserver)os).State(IBrainKeys.Id(kim), Ct))!.Value.GetRawText();
        Assert.Contains("\"cipher\"", store, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(pair.Public.SigningKey), store, StringComparison.Ordinal);
        AssertNoPrivateKey(store, pair);

        // A person of another brain is not admitted to this brain's channel at all.
        using var stranger = os.Edge.Enter(os.Edge.Person(Lee, Lee, new BrainId(Lee)));
        var denied = await Assert.ThrowsAsync<RpcException>(() => channel.ReadState(StateOf(typeof(IFriends), FriendsDefaults.KeyPart), Request()));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
    }

    // Who may use the key store: the roster of the same brain and nobody else, whatever they are.
    [Theory]
    [InlineData(Who.Person)]
    [InlineData(Who.App)]
    [InlineData(Who.Agent)]
    [InlineData(Who.Kernel)]
    public async Task OnlyTheRosterOfTheBrainUsesItsKeys(Who who)
    {
        await using var world = await World.Start();
        using (As(Kim)) { await world.Friends(Kim).Mint(); }
        var keys = SiloNeurons.Get<IBrainKeys>(world.Runtime.Services, IBrainKeys.Id(new BrainId(Kim)));
        var stored = await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart);
        var other = Pair.New();

        using (As(Kim, kind: who))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keys.PublicKeys(Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keys.Ensure(Ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keys.Adopt(other.Signing, other.Exchange, Ct));
        }

        // A person of another brain does not reach it at all.
        using (As(Lee)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => keys.PublicKeys(Ct)); }
        Assert.Equal(stored, await Stored(world, Kim, IBrainKeys.Type, IBrainKeys.KeyPart));
    }

    // The same handshake on both runtimes, with brains that never held a key in their roster.
    [Theory, MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task TwoNewBrainsExchangeTheirPublicKeys(NeuronRuntimeKind kind)
    {
        await using var os = await Journey.Start(kind, static _ => { }, Ct);
        var (alice, bob) = (os.Person("alice"), os.Person("bob"));

        var code = await alice.Get<IFriends>(FriendsDefaults.Id(alice.Brain.Value)).Mint();
        var aliceAsBobSeesHer = await bob.Get<IFriends>(FriendsDefaults.Id(bob.Brain.Value)).Redeem(code);
        var bobAsAliceSeesHim = Assert.Single(await alice.Get<IFriends>(FriendsDefaults.Id(alice.Brain.Value)).List());

        var aliceKeys = await alice.Get<IFriends>(FriendsDefaults.Id(alice.Brain.Value)).PublicKeys(Ct);
        var bobKeys = await bob.Get<IFriends>(FriendsDefaults.Id(bob.Brain.Value)).PublicKeys(Ct);
        Assert.Equal(aliceKeys.SigningKey, aliceAsBobSeesHer.PublicKeys!.SigningKey);
        Assert.Equal(aliceKeys.ExchangeKey, aliceAsBobSeesHer.PublicKeys.ExchangeKey);
        Assert.Equal(bobKeys.SigningKey, bobAsAliceSeesHim.PublicKeys!.SigningKey);
        Assert.Equal(bobKeys.ExchangeKey, bobAsAliceSeesHim.PublicKeys.ExchangeKey);
        Assert.NotEqual(aliceKeys.SigningKey, bobKeys.SigningKey);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => alice.Get<IBrainKeys>(IBrainKeys.Id(alice.Brain)).PublicKeys(Ct));
    }

    private static void AssertNoPrivateKey(string stored, Pair pair)
    {
        foreach (var secret in (byte[][])[pair.Signing, pair.Exchange])
        {
            var text = Convert.ToBase64String(secret);
            Assert.DoesNotContain(text, stored, StringComparison.Ordinal);
            // Not as the start of a longer value either, nor base64 of the base64 the sealer is given.
            Assert.DoesNotContain(text[..32], stored, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(text))[..32], stored, StringComparison.Ordinal);
        }
    }

    private static string GrainId(string brain, string type, string key) => $"neuron.host/{brain}/{type}/{key}";

    // What the brain's journal holds for the neuron, as text; empty when it holds nothing.
    private static async Task<string> Stored(World world, string brain, string type, string key)
    {
        var journal = world.Runtime.Services.GetRequiredService<IBrainJournalWriter>();
        await journal.Claim(brain, Ct);
        return await journal.ReadState(brain, GrainId(brain, type, key), "state", Ct) is { } stored ? Encoding.UTF8.GetString(stored.Record.Span) : "";
    }

    private static async Task Seed(World world, string brain, FriendsRecord roster)
    {
        var journal = world.Runtime.Services.GetRequiredService<IBrainJournalWriter>();
        await journal.Claim(brain, Ct);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(roster, FriendsJson.Default.FriendsRecord);
        await journal.Append(brain, new JournalStateWrite(GrainId(brain, FriendsDefaults.TypeName, FriendsDefaults.KeyPart), "state", JournalStateChange.Write, null, bytes, null), "", [], Ct);
    }

    // Replaces the roster's stored record while the roster sleeps.
    private static async Task Rewrite(World world, string brain, Action<FriendsRecord> change)
    {
        await world.Runtime.Deactivate(FriendsDefaults.Id(brain));
        var journal = world.Runtime.Services.GetRequiredService<IBrainJournalWriter>();
        var grainId = GrainId(brain, FriendsDefaults.TypeName, FriendsDefaults.KeyPart);
        var stored = await journal.ReadState(brain, grainId, "state", Ct) ?? throw new InvalidOperationException($"{brain} has no roster yet.");
        var roster = JsonSerializer.Deserialize(stored.Record.Span, FriendsJson.Default.FriendsRecord)!;
        change(roster);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(roster, FriendsJson.Default.FriendsRecord);
        await journal.Append(brain, new JournalStateWrite(grainId, "state", JournalStateChange.Write, stored.ETag, bytes, stored.StateType), "", [], Ct);
    }

    // Opens the sealed private keys of a stored key record with the host's sealer, as the identity neuron does.
    private static async Task<(byte[] Signing, byte[] Exchange)> Open(World world, string brain, string stored)
    {
        var state = JsonSerializer.Deserialize(stored, BrainKeysJson.Default.BrainKeysState)!;
        var sealer = world.Runtime.Services.GetRequiredService<IBrainSecretSealer>();
        Assert.IsType<IntoChat.OS.Modules.Identity.Secrets.BrainSecretSealer>(sealer);
        return (Convert.FromBase64String(await sealer.Open(brain, state.SigningKey!)), Convert.FromBase64String(await sealer.Open(brain, state.ExchangeKey!)));
    }

    // The contract table the kernel composes, over the in-memory runtime's own state and instance reads.
    private sealed class ContractTable(IBrainObserver inner, BrainType[] types) : IBrainObserver
    {
        public BrainType[] Listed => types;

        public Task<BrainType[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);
        public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class => inner.Instances<T>(cancellationToken);
        public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default) => inner.Instances(type, cancellationToken);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => inner.Brains(cancellationToken);
        public Task<ObservedWire[]> Wires(CancellationToken cancellationToken = default) => inner.Wires(cancellationToken);
        public IAsyncEnumerable<SignalDelivery> Deliveries(CancellationToken cancellationToken = default) => inner.Deliveries(cancellationToken);
        public Task<JsonElement?> State(NeuronId id, CancellationToken cancellationToken = default) => inner.State(id, cancellationToken);
    }

    // The service logs what it does not tell the caller; a failing fact shows it.
    private sealed class ChannelLog : ILogger<NeuronsGrpcService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) { TestContext.Current.TestOutputHelper?.WriteLine($"{logLevel}: {formatter(state, exception)} {exception}"); }
        }
    }

    // The membership rule of the edge: a person reaches the brain of the same name.
    private sealed class OwnBrainOnly : IBrainAccess
    {
        public ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(string.Equals(accountId, brainId, StringComparison.Ordinal));
    }

    private sealed class RequestCall : ServerCallContext
    {
        private readonly Dictionary<object, object> _state = [];

        public RequestCall(HttpContext http) => _state["__HttpContext"] = http;

        protected override string MethodCore => "/intochat.Neurons/ReadState";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore { get; } = [];
        protected override CancellationToken CancellationTokenCore => Ct;
        protected override Metadata ResponseTrailersCore { get; } = [];
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } = new(null, []);
        protected override IDictionary<object, object> UserStateCore => _state;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
