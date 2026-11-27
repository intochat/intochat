using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Keys;
using IntoChat.OS.Modules.Identity.Keys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Kernel;

[Shareable, Offered]
[NeuronAlias("facts.kernel.relay.note"), DefaultNeuronType("facts.kernel.relay.note")]
public interface IKernelRelayEcho
{
    [Shared, ReadOnlyCall] Task<string> Echo(string text);
}

public sealed class KernelRelayEchoState;

[Neuron("facts.kernel.relay.note")]
public sealed class KernelRelayEchoNeuron : Neuron<KernelRelayEchoState>, IKernelRelayEcho
{
    [ReadOnlyCall]
    public Task<string> Echo(string text) => Task.FromResult($"echo:{text}");
}

public sealed class BrainRelayTransportKernelFacts
{
    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    [Fact]
    public async Task KernelDiResolvesTransportAndReceiverAndDegradesCleanlyWhenRelayUnconfigured()
    {
        await using var os = TestOS.Local();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICallerAmbient>(os.Callers);
        services.AddSingleton(os.Edge);
        services.AddSingleton<IBrainKeySigner, IdentityBrainKeySigner>();
        services.AddKernelBrainRelayTransport();

        using var provider = services.BuildServiceProvider();

        var transport = provider.GetRequiredService<IBrainTransport>();
        var sealedTransport = Assert.IsType<SealedRelayBrainTransport>(transport);
        var receiver = provider.GetRequiredService<BrainEnvelopeReceiver>();
        var directory = provider.GetRequiredService<IBrainKeyDirectory>();
        var relayProvider = provider.GetRequiredService<KernelBrainRelayClientProvider>();

        Assert.NotNull(sealedTransport);
        Assert.NotNull(receiver);
        Assert.NotNull(directory);
        Assert.False(relayProvider.IsConfigured);
        Assert.Null(relayProvider.TryGetClient());
        Assert.False(transport.IsRemote(new BrainId("bob")));
        Assert.False(transport.IsRemote(new BrainId("system")));
    }

    [Fact]
    public async Task IdentityBackedNonExportableSignerSealsAndOpensEnvelopesEndToEndWithoutExportingPrivateKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        TestOS? osRef = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(_ => osRef!.Edge);
        services.AddSingleton<ICallerAmbient>(new DelegatingCallerAmbient(() => osRef!.Callers));
        services.AddSingleton(_ => osRef!.Proxies);
        services.AddSingleton<IBrainSecretSealer, InMemoryBrainSecretSealer>();
        services.AddSingleton<IBrainKeySigner, IdentityBrainKeySigner>();
        services.AddKernelBrainRelayTransport();
        services.Configure<BrainRelayOptions>(options =>
        {
            options.RelayBaseUri = new Uri("https://relay.example");
        });

        await using var provider = services.BuildServiceProvider();
        await using var os = osRef = TestOS.Local(options => options.Services = provider);
        os.Register(typeof(IdentityModule).Assembly);
        os.Register<KernelRelayEchoNeuron>();

        var signer = provider.GetRequiredService<IBrainKeySigner>();
        var directory = provider.GetRequiredService<IBrainKeyDirectory>();

        // 1. Obtain public keys through Identity-owned non-exportable signer (generates & seals keys inside BrainKeysNeuron).
        var alicePublicKeys = await signer.GetPublicKeysAsync(aliceBrain, ct);
        var bobPublicKeys = await signer.GetPublicKeysAsync(bobBrain, ct);
        Assert.NotEmpty(alicePublicKeys.SigningKey);
        Assert.NotEmpty(bobPublicKeys.SigningKey);
        Assert.False(alicePublicKeys.Matches(bobPublicKeys));

        // Direct non-Identity callers to IBrainKeysCrypto must be refused fail-closed.
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var crypto = os.Get<IBrainKeysCrypto>(IBrainKeys.Id(aliceBrain));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => crypto.SignData("hello"u8.ToArray(), ct));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => crypto.DeriveSharedSecret(bobPublicKeys.ExchangeKey, ct));
        }

        // 2. Pin each brain's public keys & account binding in the Kernel directory.
        var kernelDirectory = provider.GetRequiredService<KernelBrainKeyDirectory>();
        await directory.PinAsync(aliceBrain, alicePublicKeys, ct);
        await directory.PinAsync(bobBrain, bobPublicKeys, ct);
        kernelDirectory.PinAccount(bobBrain, "bob");

        // Fail-closed verifyCallerAccount rejects spoofed accounts and accepts the bound account.
        Assert.True(await kernelDirectory.VerifyCallerAccountAsync(bobBrain, "bob", ct));
        Assert.False(await kernelDirectory.VerifyCallerAccountAsync(bobBrain, "alice", ct));
        Assert.False(await kernelDirectory.VerifyCallerAccountAsync(new BrainId("unknown"), "bob", ct));

        // Silent key replacement for a pinned brain must fail closed.
        using var rogueKeys = BrainKeyPair.Generate();
        await Assert.ThrowsAsync<CryptographicException>(() =>
            directory.PinAsync(bobBrain, rogueKeys.ExportPublicKeys(), ct).AsTask());

        // 3. Alice offers her echo neuron to Bob.
        var echoId = new NeuronId(aliceBrain, "facts.kernel.relay.note", "echo-1");
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", aliceBrain)))
        {
            var offers = os.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(echoId, "bob", Role.Viewer, ct);
        }

        // 4. Wire BrainEnvelopeReceiver with non-null fail-closed scoped verifyCallerAccount and SealedRelayBrainTransport.
        var receiver = new BrainEnvelopeReceiver(
            isLocalBrain: brain => brain == aliceBrain,
            signer: signer,
            scopedKeyLookup: kernelDirectory.GetPinnedKeysForAsync,
            edge: os.Edge,
            invoke: os.InvokeLocalAsync,
            post: os.PostLocalAsync,
            timeProvider: clock,
            scopedVerifyCallerAccount: kernelDirectory.VerifyCallerAccountForAsync);

        var captured = new List<BrainEnvelope>();
        var transport = new SealedRelayBrainTransport(
            signer: signer,
            keyLookup: directory.GetPinnedKeysAsync,
            callers: os.Callers,
            relay: async (envelope, token) =>
            {
                captured.Add(envelope);
                var reply = await receiver.DeliverAsync(envelope, token).ConfigureAwait(false);
                captured.Add(reply);
                return reply;
            },
            isRemote: brain => brain == aliceBrain,
            localBrain: bobBrain,
            timeProvider: clock);

        // 5. Bob invokes Alice's offered neuron through SealedRelayBrainTransport; private keys never leave BrainKeysNeuron.
        using (os.Edge.Enter(os.Edge.Person("bob", "bob", bobBrain)))
        {
            var call = new RuntimeInvocation(
                Behavior: string.Empty,
                Contract: "facts.kernel.relay.note",
                Key: echoId.ToString(),
                Method: nameof(IKernelRelayEcho.Echo),
                Arguments: [JsonSerializer.SerializeToElement("non-exportable-e2ee", RuntimeProtocol.Json)]);
            var result = await transport.InvokeRemoteAsync(echoId, call, ct);
            Assert.NotNull(result);
            Assert.Equal("echo:non-exportable-e2ee", result.Value.GetString());
        }

        // 6. Spoofed CallerAccount from Bob's brain is rejected fail-closed by BrainEnvelopeReceiver.
        using (os.Edge.Enter(os.Edge.Person("alice", "alice", bobBrain)))
        {
            var call = new RuntimeInvocation(
                Behavior: string.Empty,
                Contract: "facts.kernel.relay.note",
                Key: echoId.ToString(),
                Method: nameof(IKernelRelayEcho.Echo),
                Arguments: [JsonSerializer.SerializeToElement("spoof", RuntimeProtocol.Json)]);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transport.InvokeRemoteAsync(echoId, call, ct));
        }

        // 7. Cold-start inbound delivery with empty in-memory pins and callers.Current == null reads IFriends(targetBrain).List() under module scope.
        var coldDirectory = new KernelBrainKeyDirectory(provider, callers: null);
        Assert.Null(os.Callers.Current);
        var charlieBrain = new BrainId("charlie");
        using var charlieKeys = BrainKeyPair.Generate();
        await coldDirectory.PinForAsync(aliceBrain, charlieBrain, charlieKeys.ExportPublicKeys(), ct);
        coldDirectory.PinAccountFor(aliceBrain, charlieBrain, "charlie");
        // Multi-brain isolation: charlie's pin under aliceBrain is not visible under bobBrain.
        Assert.Null(await coldDirectory.GetPinnedKeysForAsync(bobBrain, charlieBrain, ct));
        Assert.False(await coldDirectory.VerifyCallerAccountForAsync(bobBrain, charlieBrain, "charlie", ct));
        Assert.True(await coldDirectory.VerifyCallerAccountForAsync(aliceBrain, charlieBrain, "charlie", ct));

        Assert.True(captured.Count >= 2);
        foreach (var envelope in captured)
        {
            Assert.DoesNotContain("non-exportable-e2ee", Encoding.UTF8.GetString(envelope.Ciphertext), StringComparison.Ordinal);
        }
    }
}
