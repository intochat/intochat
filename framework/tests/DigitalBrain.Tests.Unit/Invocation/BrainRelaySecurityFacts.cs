using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class BrainRelaySecurityFacts
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? instance : null;
    }

    [Fact]
    public void FriendCodeBundleSelfSignatureVerifiesAndRejectsTampering()
    {
        using var aliceKeys = BrainKeyPair.Generate();
        using var attackerKeys = BrainKeyPair.Generate();
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        var bundle = FriendCodeBundleSigner.Sign(
            code: "ABCD-EFGH",
            brainId: new BrainId("alice"),
            displayName: "Alice",
            relayEndpoint: "https://cloud.example/circle/v1",
            keys: aliceKeys,
            issuedAtUtc: now,
            expiresAtUtc: now.AddHours(24));

        Assert.True(FriendCodeBundleSigner.Verify(bundle, now));
        Assert.True(FriendCodeBundleSigner.Verify(bundle, now.AddHours(12)));

        // Expired bundle rejected.
        Assert.False(FriendCodeBundleSigner.Verify(bundle, now.AddHours(24).AddSeconds(1)));

        // Tampered code, brain ID, display name, relay endpoint, public keys, timestamps, or signature -> rejected.
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { Code = "XXXX-YYYY" }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { BrainId = new BrainId("mallory") }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { DisplayName = "Mallory" }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { RelayEndpoint = "https://evil.example" }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { PublicKeys = attackerKeys.ExportPublicKeys() }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { ExpiresAtUtc = now.AddHours(48) }, now));

        var badSig = (byte[])bundle.Signature.Clone();
        badSig[0] ^= 0xFF;
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { Signature = badSig }, now));
        Assert.Throws<CryptographicException>(() => FriendCodeBundleSigner.VerifyOrThrow(bundle with { Signature = badSig }, now));
    }

    [Fact]
    public async Task OneWayPostAndRequestResponseRoundTripAcrossTwoBrains()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();

        var aliceDir = new InMemoryPinnedBrainKeyDirectory();
        var bobDir = new InMemoryPinnedBrainKeyDirectory();
        await aliceDir.PinAsync(bobBrain, bobKeys.ExportPublicKeys(), ct);
        await bobDir.PinAsync(aliceBrain, aliceKeys.ExportPublicKeys(), ct);

        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();

        var aliceReceiver = new BrainEnvelopeReceiver(
            brain => brain == aliceBrain,
            new BrainKeyPairSigner(aliceKeys),
            aliceDir.GetPinnedKeysAsync,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token),
            (target, call) => aliceOS.PostLocalAsync(target, call),
            timeProvider: clock);

        var oneWayPosted = new List<BrainEnvelope>();
        var rpcSent = new List<BrainEnvelope>();

        TestOS? bobOSRef = null;
        var bobTransport = new SealedRelayBrainTransport(
            signer: new BrainKeyPairSigner(bobKeys),
            keyLookup: bobDir.GetPinnedKeysAsync,
            callers: new DelegatingCallerAmbient(() => bobOSRef!.Callers),
            relay: async (envelope, token) =>
            {
                rpcSent.Add(envelope);
                return await aliceReceiver.DeliverAsync(envelope, token).ConfigureAwait(false);
            },
            postRelay: async (envelope, token) =>
            {
                oneWayPosted.Add(envelope);
                _ = await aliceReceiver.DeliverAsync(envelope, token).ConfigureAwait(false);
            },
            isRemote: brain => !brain.IsSystem && brain != bobBrain,
            localBrain: bobBrain,
            timeProvider: clock);

        await using var bobOS = TestOS.Local(options =>
        {
            options.Services = new SingleServiceProvider(typeof(IBrainTransport), bobTransport);
        });
        bobOSRef = bobOS;

        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "shared-note");
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(noteId, "bob", Role.Editor, ct);
        }

        // 1. OneWay PostRemoteAsync uses postRelay with OneWay=true and does not block on RPC relay.
        using (bobOS.Edge.Enter(bobOS.Edge.Person("bob", "bob", bobBrain)))
        {
            var writeCall = new RuntimeInvocation(
                Behavior: string.Empty,
                Contract: "facts.transport.note",
                Key: noteId.ToString(),
                Method: nameof(IRemoteSecretNote.Write),
                Arguments: [JsonSerializer.SerializeToElement("one-way-hello", RuntimeProtocol.Json)]);
            await bobTransport.PostRemoteAsync(noteId, writeCall, ct);

            var posted = Assert.Single(oneWayPosted);
            Assert.True(posted.OneWay);
            Assert.Equal(clock.GetUtcNow(), posted.SentAtUtc);
            Assert.Empty(rpcSent);

            // 2. Request-response Read verifies the one-way write took effect.
            var read = await bobOS.Get<IRemoteSecretNote>(noteId).Read();
            Assert.Equal("one-way-hello", read);
            var rpcEnvelope = Assert.Single(rpcSent);
            Assert.False(rpcEnvelope.OneWay);
            Assert.Equal(clock.GetUtcNow(), rpcEnvelope.SentAtUtc);
        }
    }

    [Fact]
    public async Task ReceiverRejectsReplayedMessageIdAndOutOfWindowSentAtUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseTime = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(baseTime);
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();

        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();
        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "replay-note");
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            await aliceOS.Get<IRemoteSecretNote>(noteId).Write("secret");
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(noteId, "bob", Role.Viewer, ct);
        }

        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token),
            clock: clock);

        var request = new RemoteInvocationRequest(
            noteId,
            new RuntimeInvocation(Behavior: string.Empty, Contract: "facts.transport.note", Key: noteId.ToString(), Method: nameof(IRemoteSecretNote.Read), Arguments: []),
            "bob",
            "bob",
            bobBrain);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json);

        // 1. Stale envelope (-6 minutes) -> rejected before replay cache is polluted.
        var staleEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-stale",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: baseTime.AddMinutes(-6));
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(staleEnvelope, ct));

        // 2. Future envelope (+6 minutes) -> rejected.
        var futureEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-future",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: baseTime.AddMinutes(6));
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(futureEnvelope, ct));

        // 3. Missing SentAtUtc -> rejected.
        var missingTimestamp = staleEnvelope with { SentAtUtc = null };
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(missingTimestamp, ct));

        // 4. Fresh envelope within ±5m skew (-4m) -> accepted once, exact replay rejected!
        var validEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-valid-1",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: baseTime.AddMinutes(-4));
        var reply = await receiver.DeliverAsync(validEnvelope, ct);
        Assert.Equal("msg-valid-1", reply.ReplyToMessageId);

        // Exact replay with same MessageId -> rejected with CryptographicException!
        var replayError = await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(validEnvelope, ct));
        Assert.Contains("replay", replayError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImmutableKeyPinningRejectsSilentKeyChangeAndAcceptsSignedRotationProof()
    {
        var ct = TestContext.Current.CancellationToken;
        var bobBrain = new BrainId("bob");
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

        using var bobOldKeys = BrainKeyPair.Generate();
        using var bobNewKeys = BrainKeyPair.Generate();
        using var attackerKeys = BrainKeyPair.Generate();

        var directory = new InMemoryPinnedBrainKeyDirectory();
        await directory.PinAsync(bobBrain, bobOldKeys.ExportPublicKeys(), ct);

        // Idempotent re-pin of identical keys succeeds.
        await directory.PinAsync(bobBrain, bobOldKeys.ExportPublicKeys(), ct);

        // Silent replacement with different keys is rejected fail-closed.
        await Assert.ThrowsAsync<CryptographicException>(() =>
            directory.PinAsync(bobBrain, bobNewKeys.ExportPublicKeys(), ct).AsTask());

        // Forged rotation proof signed by attacker is rejected.
        var forgedProof = BrainKeyRotationSigner.Sign(bobBrain, attackerKeys, bobNewKeys.ExportPublicKeys(), now);
        await Assert.ThrowsAsync<CryptographicException>(() => directory.RotateAsync(forgedProof, ct).AsTask());
        Assert.True((await directory.GetPinnedKeysAsync(bobBrain, ct))!.Matches(bobOldKeys.ExportPublicKeys()));

        // Valid rotation proof signed by previous pinned key succeeds and updates the pin.
        var validProof = BrainKeyRotationSigner.Sign(bobBrain, bobOldKeys, bobNewKeys.ExportPublicKeys(), now);
        await directory.RotateAsync(validProof, ct);
        Assert.True((await directory.GetPinnedKeysAsync(bobBrain, ct))!.Matches(bobNewKeys.ExportPublicKeys()));

        // Envelope signed with old key is now rejected by BrainEnvelopeReceiver against the rotated pin.
        using var aliceKeys = BrainKeyPair.Generate();
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            brain => brain == new BrainId("alice"),
            new BrainKeyPairSigner(aliceKeys),
            directory.GetPinnedKeysAsync,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token));

        var oldKeyEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            new BrainId("alice"),
            "msg-old-key",
            "{}"u8,
            bobOldKeys,
            aliceKeys.ExportPublicKeys());
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(oldKeyEnvelope, ct));
    }

    [Fact]
    public async Task HttpBrainRelayClientVerifiesFriendCodeBundleAndRejectsRelayMitm()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        using var aliceKeys = BrainKeyPair.Generate();
        using var mitmKeys = BrainKeyPair.Generate();

        var honestBundle = FriendCodeBundleSigner.Sign(
            "JOIN-1234",
            new BrainId("alice"),
            "Alice",
            "https://relay.example",
            aliceKeys,
            now,
            now.AddHours(24));

        var tamperMode = false;
        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/circle/v1/friends/codes/publish")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/circle/v1/friends/codes/JOIN-1234")
            {
                var returned = tamperMode
                    ? honestBundle with { PublicKeys = mitmKeys.ExportPublicKeys() }
                    : honestBundle;
                var json = JsonSerializer.Serialize(returned, RuntimeJsonContext.Default.FriendCodeBundle);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(handler);
        var client = new HttpBrainRelayClient(http, new Uri("https://relay.example"), timeProvider: clock);

        await client.PublishFriendCodeAsync(honestBundle, ct);

        var resolved = await client.ResolveFriendCodeAsync("JOIN-1234", ct);
        Assert.NotNull(resolved);
        Assert.Equal("alice", resolved.BrainId.Value);
        Assert.True(resolved.PublicKeys.Matches(aliceKeys.ExportPublicKeys()));

        // Relay attempts MITM by swapping public keys -> client rejects with CryptographicException!
        tamperMode = true;
        await Assert.ThrowsAsync<CryptographicException>(() => client.ResolveFriendCodeAsync("JOIN-1234", ct));
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
