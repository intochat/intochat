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
        var expiresAt = now.AddHours(24);

        var bundle = FriendCodeBundleSigner.Sign(
            codeHash: "hash-abcd-efgh",
            brainId: new BrainId("alice"),
            keys: aliceKeys,
            expiresAt: expiresAt,
            relayOrigin: "https://cloud.example");

        var canonical = FriendCodeBundleSigner.BuildCanonicalPayload(
            bundle.CodeHash,
            bundle.BrainId,
            bundle.SigningPublicKey,
            bundle.ExchangePublicKey,
            expiresAt);
        Assert.StartsWith("intochat-friend-code-v1\nalice\n", canonical, StringComparison.Ordinal);
        Assert.EndsWith($"\nhash-abcd-efgh\n{expiresAt.ToUnixTimeSeconds()}", canonical, StringComparison.Ordinal);

        Assert.True(FriendCodeBundleSigner.Verify(bundle, now));
        Assert.True(FriendCodeBundleSigner.Verify(bundle, now.AddHours(12)));

        // Expired bundle rejected.
        Assert.False(FriendCodeBundleSigner.Verify(bundle, expiresAt.AddSeconds(1)));

        // Tampered codeHash, brainId, public keys, expiry, or signature -> rejected.
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { CodeHash = "hash-xxxx-yyyy" }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { BrainId = new BrainId("mallory") }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { SigningPublicKey = attackerKeys.ExportPublicKeys().SigningKey }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { ExchangePublicKey = attackerKeys.ExportPublicKeys().ExchangeKey }, now));
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { ExpiresAt = now.AddHours(48) }, now));

        var badSig = (byte[])bundle.SelfSignature.Clone();
        badSig[0] ^= 0xFF;
        Assert.False(FriendCodeBundleSigner.Verify(bundle with { SelfSignature = badSig }, now));
        Assert.Throws<CryptographicException>(() => FriendCodeBundleSigner.VerifyOrThrow(bundle with { SelfSignature = badSig }, now));
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
    public async Task HttpBrainRelayClientVerifiesFriendCodeBundleAndAcksOneWayLeasedEnvelopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(now);
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        using var mitmKeys = BrainKeyPair.Generate();

        var honestBundle = FriendCodeBundleSigner.Sign(
            codeHash: "hash-join-1234",
            brainId: new BrainId("alice"),
            keys: aliceKeys,
            expiresAt: now.AddHours(24),
            relayOrigin: "https://relay.example");

        var tamperMode = false;
        string? ackedPath = null;
        string? ackedLeaseId = null;

        var oneWayRequest = new RemoteInvocationRequest(
            new NeuronId(new BrainId("alice"), "facts.transport.note", "n1"),
            new RuntimeInvocation(Behavior: string.Empty, Contract: "facts.transport.note", Key: "alice/facts.transport.note/n1", Method: nameof(IRemoteSecretNote.Write), Arguments: [JsonSerializer.SerializeToElement("hi", RuntimeProtocol.Json)]),
            "bob",
            "bob",
            new BrainId("bob"),
            OneWay: true);
        var oneWayEnvelope = BrainEnvelopeSealer.Seal(
            new BrainId("bob"),
            new BrainId("alice"),
            "msg-oneway-ack-1",
            JsonSerializer.SerializeToUtf8Bytes(oneWayRequest, RuntimeProtocol.Json),
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: now,
            oneWay: true);

        using var handler = new StubHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/circle/v1/friends/codes")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/circle/v1/friends/codes/resolve")
            {
                var returned = new ResolveFriendCodeResponse(
                    BrainId: honestBundle.BrainId,
                    SigningPublicKey: tamperMode ? mitmKeys.ExportPublicKeys().SigningKey : honestBundle.SigningPublicKey,
                    ExchangePublicKey: honestBundle.ExchangePublicKey,
                    SelfSignature: honestBundle.SelfSignature,
                    RelayOrigin: honestBundle.RelayOrigin,
                    ExpiresAt: honestBundle.ExpiresAt);
                var json = JsonSerializer.Serialize(returned, RuntimeJsonContext.Default.ResolveFriendCodeResponse);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }

            if (request.Method == HttpMethod.Delete && request.RequestUri?.AbsolutePath == "/circle/v1/friends/codes/hash-join-1234")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/circle/v1/brains/next")
            {
                var json = JsonSerializer.Serialize(oneWayEnvelope, RuntimeJsonContext.Default.BrainEnvelope);
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                resp.Headers.TryAddWithoutValidation("X-Brain-Lease-Id", "lease-42");
                return Task.FromResult(resp);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/circle/v1/brains/ack/msg-oneway-ack-1")
            {
                ackedPath = request.RequestUri.AbsolutePath;
                ackedLeaseId = request.Headers.TryGetValues("X-Brain-Lease-Id", out var vals) ? vals.FirstOrDefault() : null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        using var http = new HttpClient(handler);
        var client = new HttpBrainRelayClient(http, new Uri("https://relay.example"), timeProvider: clock);

        await client.PublishFriendCodeAsync(honestBundle, ct);

        var resolved = await client.ResolveFriendCodeAsync("hash-join-1234", ct);
        Assert.NotNull(resolved);
        Assert.Equal("alice", resolved.BrainId.Value);
        Assert.True(resolved.ToPublicKeys().Matches(aliceKeys.ExportPublicKeys()));

        // Relay attempts MITM by swapping public keys -> client rejects with CryptographicException!
        tamperMode = true;
        await Assert.ThrowsAsync<CryptographicException>(() => client.ResolveFriendCodeAsync("hash-join-1234", ct));

        await client.RevokeFriendCodeAsync("hash-join-1234", ct);

        // OneWay leased envelope triggers explicit POST /circle/v1/brains/ack/{messageId} with X-Brain-Lease-Id.
        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();
        var receiver = new BrainEnvelopeReceiver(
            new BrainId("alice"),
            aliceKeys,
            brain => brain == new BrainId("bob") ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token),
            (target, call) => aliceOS.PostLocalAsync(target, call),
            clock: clock);

        Assert.True(await client.PollOnceAsync(receiver, ct));
        Assert.Equal("/circle/v1/brains/ack/msg-oneway-ack-1", ackedPath);
        Assert.Equal("lease-42", ackedLeaseId);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
