using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Invocation;

public sealed class BrainTransportSecurityFacts
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }

    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private static BrainEnvelope SealRequest(
        BrainId from,
        BrainId to,
        BrainKeyPair senderKeys,
        BrainPublicKeys recipientPublicKeys,
        RemoteInvocationRequest request,
        string messageId = "msg-1",
        DateTimeOffset? sentAtUtc = null)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeProtocol.Json);
        return BrainEnvelopeSealer.Seal(
            from,
            to,
            messageId,
            plaintext,
            senderKeys,
            recipientPublicKeys,
            sentAtUtc: sentAtUtc ?? FixedNow);
    }

    private static RemoteInvocationResponse OpenResponse(
        BrainEnvelope reply,
        BrainKeyPair senderKeys,
        BrainPublicKeys receiverPublicKeys)
    {
        var plaintext = BrainEnvelopeSealer.Open(reply, senderKeys, receiverPublicKeys);
        return JsonSerializer.Deserialize<RemoteInvocationResponse>(plaintext, RuntimeProtocol.Json)
            ?? throw new InvalidDataException("Reply payload was null.");
    }

    private static RemoteInvocationRequest CreateRequest(
        BrainId targetBrain,
        BrainId callerBrain,
        string callerAccount = "bob",
        string callerPrincipal = "bob")
        => new(
            Target: new NeuronId(targetBrain, "facts.transport.note", "note-1"),
            Call: new RuntimeInvocation("facts.transport.note", nameof(IRemoteSecretNote), "note-1", "Read", []),
            CallerAccount: callerAccount,
            CallerPrincipal: callerPrincipal,
            CallerBrain: callerBrain,
            OneWay: false);

    [Fact]
    public void TamperedEnvelopeFieldsThrowCryptographicException()
    {
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();

        var plaintext = "sensitive-payload"u8.ToArray();
        var envelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-tamper-1",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: FixedNow);

        // Sanity check: unmodified envelope opens cleanly and preserves SentAtUtc.
        Assert.Equal(FixedNow, envelope.SentAtUtc);
        Assert.Equal(plaintext, BrainEnvelopeSealer.Open(envelope, aliceKeys, bobKeys.ExportPublicKeys()));

        // 1. Tampered Ciphertext
        var badCiphertext = (byte[])envelope.Ciphertext.Clone();
        badCiphertext[0] ^= 0x01;
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { Ciphertext = badCiphertext }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 2. Tampered Signature
        var badSignature = (byte[])envelope.Signature.Clone();
        badSignature[0] ^= 0x01;
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { Signature = badSignature }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 3. Tampered From
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { From = new BrainId("mallory") }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 4. Tampered To
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { To = new BrainId("charlie") }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 5. Tampered MessageId
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { MessageId = "msg-tamper-2" }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 6. Tampered SentAtUtc (shifted by 1 second, still within skew window)
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { SentAtUtc = FixedNow.AddSeconds(1) }, aliceKeys, bobKeys.ExportPublicKeys()));

        // 7. Stripped SentAtUtc (null)
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(envelope with { SentAtUtc = null }, aliceKeys, bobKeys.ExportPublicKeys()));
    }

    [Fact]
    public void EnvelopeWithSentAtUtcRoundTripsThroughJsonAndOpensCleanly()
    {
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();

        var sentAt = new DateTimeOffset(2026, 11, 27, 12, 34, 56, 789, TimeSpan.Zero).AddTicks(1234);
        var plaintext = "roundtrip-payload"u8.ToArray();
        var envelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-json-1",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            sentAtUtc: sentAt);

        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, RuntimeProtocol.Json);
        var deserialized = JsonSerializer.Deserialize<BrainEnvelope>(json, RuntimeProtocol.Json);

        Assert.NotNull(deserialized);
        Assert.Equal(sentAt, deserialized.SentAtUtc);
        Assert.Equal(plaintext, BrainEnvelopeSealer.Open(deserialized, aliceKeys, bobKeys.ExportPublicKeys()));
    }

    [Fact]
    public async Task ReplayedMessageIdThrowsCryptographicExceptionAndDoesNotReinvokeTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("ok", RuntimeProtocol.Json));
            },
            clock: clock);

        var request = CreateRequest(aliceBrain, bobBrain);
        var envelope = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), request, messageId: "msg-replay-1", sentAtUtc: FixedNow);

        // First delivery succeeds.
        var reply = await receiver.DeliverAsync(envelope, ct);
        var response = OpenResponse(reply, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Null(response.ErrorType);
        Assert.Equal(1, invokeCount);

        // Replaying the exact same envelope fails with CryptographicException and never invokes the target again.
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(envelope, ct));
        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public async Task InvalidSignatureDoesNotPoisonReplayCacheForSubsequentValidEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        using var attackerKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("ok", RuntimeProtocol.Json));
            },
            clock: clock);

        const string sharedMessageId = "msg-poison-attempt";
        var request = CreateRequest(aliceBrain, bobBrain);

        // Attacker sends forged envelope claiming to be from Bob with the target MessageId.
        var forgedEnvelope = SealRequest(bobBrain, aliceBrain, attackerKeys, aliceKeys.ExportPublicKeys(), request, messageId: sharedMessageId, sentAtUtc: FixedNow);
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(forgedEnvelope, ct));
        Assert.Equal(0, invokeCount);

        // Legitimate Bob later sends a valid envelope with the same MessageId -> must succeed!
        var validEnvelope = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), request, messageId: sharedMessageId, sentAtUtc: FixedNow);
        var reply = await receiver.DeliverAsync(validEnvelope, ct);
        var response = OpenResponse(reply, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Null(response.ErrorType);
        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public async Task ExpiredFutureSkewedAndMissingSentAtUtcThrowCryptographicException()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("ok", RuntimeProtocol.Json));
            },
            clock: clock);

        var request = CreateRequest(aliceBrain, bobBrain);

        // 1. Expired (-6 minutes, outside default 5-minute skew)
        var expiredEnvelope = SealRequest(
            bobBrain,
            aliceBrain,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            request,
            messageId: "msg-expired",
            sentAtUtc: FixedNow.AddMinutes(-6));
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(expiredEnvelope, ct));

        // 2. Future-skewed (+6 minutes, outside default 5-minute skew)
        var futureEnvelope = SealRequest(
            bobBrain,
            aliceBrain,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            request,
            messageId: "msg-future",
            sentAtUtc: FixedNow.AddMinutes(6));
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(futureEnvelope, ct));

        // 3. Missing SentAtUtc (null)
        var missingTimestampEnvelope = SealRequest(
            bobBrain,
            aliceBrain,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            request,
            messageId: "msg-missing-ts",
            sentAtUtc: FixedNow) with { SentAtUtc = null };
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(missingTimestampEnvelope, ct));

        Assert.Equal(0, invokeCount);

        // 4. Within skew (-4 minutes) succeeds.
        var freshEnvelope = SealRequest(
            bobBrain,
            aliceBrain,
            bobKeys,
            aliceKeys.ExportPublicKeys(),
            request,
            messageId: "msg-fresh",
            sentAtUtc: FixedNow.AddMinutes(-4));
        var reply = await receiver.DeliverAsync(freshEnvelope, ct);
        Assert.Null(OpenResponse(reply, bobKeys, aliceKeys.ExportPublicKeys()).ErrorType);
        Assert.Equal(1, invokeCount);
    }

    [Fact]
    public async Task StaleOrMissingReplyTimestampRejectedByTransport()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var publicDirectory = new Dictionary<BrainId, BrainPublicKeys>
        {
            [aliceBrain] = aliceKeys.ExportPublicKeys(),
            [bobBrain] = bobKeys.ExportPublicKeys(),
        };

        await using var bobOS = TestOS.Local();

        BrainEnvelope BuildReply(BrainEnvelope req, DateTimeOffset? replyTimestamp, bool stripTimestamp = false)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(
                new RemoteInvocationResponse(Result: JsonSerializer.SerializeToElement("ok", RuntimeProtocol.Json)),
                RuntimeProtocol.Json);
            var sealedReply = BrainEnvelopeSealer.Seal(
                aliceBrain,
                bobBrain,
                Guid.NewGuid().ToString("N"),
                payload,
                aliceKeys,
                bobKeys.ExportPublicKeys(),
                replyToMessageId: req.MessageId,
                sentAtUtc: replyTimestamp);
            return stripTimestamp ? sealedReply with { SentAtUtc = null } : sealedReply;
        }

        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "note-1");
        var call = new RuntimeInvocation("facts.transport.note", nameof(IRemoteSecretNote), "note-1", "Read", []);

        using (bobOS.Edge.Enter(bobOS.Edge.Person("bob", "bob", bobBrain)))
        {
            var expiredReplyTransport = new SealedRelayBrainTransport(
                bobBrain,
                bobKeys,
                brain => publicDirectory.TryGetValue(brain, out var k) ? k : null,
                new DelegatingCallerAmbient(() => bobOS.Callers),
                (req, _) => Task.FromResult(BuildReply(req, FixedNow.AddMinutes(-6))),
                clock: clock);
            await Assert.ThrowsAsync<CryptographicException>(() => expiredReplyTransport.InvokeRemoteAsync(noteId, call, ct));

            var futureReplyTransport = new SealedRelayBrainTransport(
                bobBrain,
                bobKeys,
                brain => publicDirectory.TryGetValue(brain, out var k) ? k : null,
                new DelegatingCallerAmbient(() => bobOS.Callers),
                (req, _) => Task.FromResult(BuildReply(req, FixedNow.AddMinutes(6))),
                clock: clock);
            await Assert.ThrowsAsync<CryptographicException>(() => futureReplyTransport.InvokeRemoteAsync(noteId, call, ct));

            var missingTimestampReplyTransport = new SealedRelayBrainTransport(
                bobBrain,
                bobKeys,
                brain => publicDirectory.TryGetValue(brain, out var k) ? k : null,
                new DelegatingCallerAmbient(() => bobOS.Callers),
                (req, _) => Task.FromResult(BuildReply(req, FixedNow, stripTimestamp: true)),
                clock: clock);
            await Assert.ThrowsAsync<CryptographicException>(() => missingTimestampReplyTransport.InvokeRemoteAsync(noteId, call, ct));
        }
    }

    [Fact]
    public async Task CallerBrainOrTargetBrainMismatchReturnsUnauthorizedAccessException()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        var charlieBrain = new BrainId("charlie");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(null);
            },
            clock: clock);

        // 1. CallerBrain != envelope.From (Bob claims CallerBrain = Charlie)
        var spoofedCallerBrainReq = CreateRequest(targetBrain: aliceBrain, callerBrain: charlieBrain);
        var spoofedCallerBrainEnv = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), spoofedCallerBrainReq, messageId: "msg-mismatch-caller", sentAtUtc: FixedNow);
        var reply1 = await receiver.DeliverAsync(spoofedCallerBrainEnv, ct);
        var resp1 = OpenResponse(reply1, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Equal(nameof(UnauthorizedAccessException), resp1.ErrorType);

        // 2. Target.Brain != localBrain (envelope addressed to Alice, inner target addressed to Charlie)
        var wrongTargetBrainReq = CreateRequest(targetBrain: charlieBrain, callerBrain: bobBrain);
        var wrongTargetBrainEnv = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), wrongTargetBrainReq, messageId: "msg-mismatch-target", sentAtUtc: FixedNow);
        var reply2 = await receiver.DeliverAsync(wrongTargetBrainEnv, ct);
        var resp2 = OpenResponse(reply2, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Equal(nameof(UnauthorizedAccessException), resp2.ErrorType);

        Assert.Equal(0, invokeCount);
    }

    [Fact]
    public async Task SpoofedCallerAccountRejectedByVerifyCallerAccountHookNeverInvokesTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        var verifiedPairs = new List<(BrainId Brain, string Account)>();
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement("ok", RuntimeProtocol.Json));
            },
            clock: clock,
            verifyCallerAccount: (fromBrain, account, _) =>
            {
                verifiedPairs.Add((fromBrain, account));
                return ValueTask.FromResult(fromBrain == bobBrain && string.Equals(account, "bob-owner", StringComparison.Ordinal));
            });

        // Spoofed account ("alice-admin") from Bob's brain -> refused with UnauthorizedAccessException.
        var spoofedReq = CreateRequest(aliceBrain, bobBrain, callerAccount: "alice-admin", callerPrincipal: "alice-admin");
        var spoofedEnv = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), spoofedReq, messageId: "msg-spoof-acct", sentAtUtc: FixedNow);
        var spoofedReply = await receiver.DeliverAsync(spoofedEnv, ct);
        var spoofedResp = OpenResponse(spoofedReply, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Equal(nameof(UnauthorizedAccessException), spoofedResp.ErrorType);
        Assert.Equal(0, invokeCount);
        Assert.Single(verifiedPairs);
        Assert.Equal((bobBrain, "alice-admin"), verifiedPairs[0]);

        // Authorized account ("bob-owner") from Bob's brain -> succeeds.
        var validReq = CreateRequest(aliceBrain, bobBrain, callerAccount: "bob-owner", callerPrincipal: "bob-owner");
        var validEnv = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), validReq, messageId: "msg-valid-acct", sentAtUtc: FixedNow);
        var validReply = await receiver.DeliverAsync(validEnv, ct);
        var validResp = OpenResponse(validReply, bobKeys, aliceKeys.ExportPublicKeys());
        Assert.Null(validResp.ErrorType);
        Assert.Equal(1, invokeCount);
        Assert.Equal(2, verifiedPairs.Count);
        Assert.Equal((bobBrain, "bob-owner"), verifiedPairs[1]);
    }

    [Theory]
    [InlineData("", "bob")]
    [InlineData("   ", "bob")]
    [InlineData("bob", "")]
    [InlineData("bob", "   ")]
    public async Task EmptyOrWhitespaceCallerAccountOrPrincipalReturnsUnauthorizedAccessException(
        string callerAccount,
        string callerPrincipal)
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var clock = new ManualTimeProvider(FixedNow);

        var invokeCount = 0;
        var hookCalled = false;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (_, _, _) =>
            {
                invokeCount++;
                return Task.FromResult<JsonElement?>(null);
            },
            clock: clock,
            verifyCallerAccount: (_, _, _) =>
            {
                hookCalled = true;
                return ValueTask.FromResult(true);
            });

        var request = CreateRequest(aliceBrain, bobBrain, callerAccount: callerAccount, callerPrincipal: callerPrincipal);
        var envelope = SealRequest(bobBrain, aliceBrain, bobKeys, aliceKeys.ExportPublicKeys(), request, messageId: $"msg-empty-{Guid.NewGuid():N}", sentAtUtc: FixedNow);

        var reply = await receiver.DeliverAsync(envelope, ct);
        var response = OpenResponse(reply, bobKeys, aliceKeys.ExportPublicKeys());

        Assert.Equal(nameof(UnauthorizedAccessException), response.ErrorType);
        Assert.Equal(0, invokeCount);
        Assert.False(hookCalled);
    }
}
