using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Access;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;

namespace DigitalBrain.Tests.Unit.Invocation;

[Shareable, Offered]
[NeuronAlias("facts.transport.note"), DefaultNeuronType("facts.transport.note")]
public interface IRemoteSecretNote
{
    [Shared, ReadOnlyCall] Task<string> Read();
    [Shared] Task Write(string text);
}

public sealed record RemoteSecretNoteState(string Text)
{
    public RemoteSecretNoteState() : this(string.Empty) { }
}

[Signal("facts.transport.note-changed")]
public sealed record RemoteSecretNoteChanged(string Text) : Signal;

[Neuron("facts.transport.note")]
public sealed class RemoteSecretNoteNeuron : Neuron<RemoteSecretNoteState>, IRemoteSecretNote
{
    [ReadOnlyCall]
    public Task<string> Read() => Task.FromResult(State.Text);

    public async Task Write(string text)
    {
        await Save(new RemoteSecretNoteState(text), new RemoteSecretNoteChanged(text)).ConfigureAwait(true);
    }
}

public sealed class BrainTransportFacts
{
    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private sealed class SingleServiceProvider(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? instance : null;
    }

    [Fact]
    public async Task TwoSeparateTestOSInstancesExchangeOfferedCallsThroughSealedOpaqueRelay()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        var publicDirectory = new Dictionary<BrainId, BrainPublicKeys>
        {
            [aliceBrain] = aliceKeys.ExportPublicKeys(),
            [bobBrain] = bobKeys.ExportPublicKeys(),
        };

        await using var aliceOS = TestOS.Local();
        aliceOS.Register<RemoteSecretNoteNeuron>();

        var aliceReceiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => publicDirectory.TryGetValue(brain, out var keys) ? keys : null,
            aliceOS.Edge,
            (target, call, token) => aliceOS.InvokeLocalAsync(target, call, token),
            (target, call) => aliceOS.PostLocalAsync(target, call));

        var capturedEnvelopes = new List<BrainEnvelope>();

        Task<BrainEnvelope> OpaqueRelay(BrainEnvelope request, CancellationToken token)
        {
            capturedEnvelopes.Add(request);
            return DeliverAndCaptureAsync(request, token);
        }

        async Task<BrainEnvelope> DeliverAndCaptureAsync(BrainEnvelope request, CancellationToken token)
        {
            Assert.Equal(aliceBrain, request.To);
            var response = await aliceReceiver.DeliverAsync(request, token).ConfigureAwait(false);
            capturedEnvelopes.Add(response);
            return response;
        }

        TestOS? bobOSRef = null;
        var bobTransport = new SealedRelayBrainTransport(
            bobBrain,
            bobKeys,
            brain => publicDirectory.TryGetValue(brain, out var keys) ? keys : null,
            new DelegatingCallerAmbient(() => bobOSRef!.Callers),
            OpaqueRelay);

        await using var bobOS = TestOS.Local(options =>
        {
            options.Services = new SingleServiceProvider(typeof(IBrainTransport), bobTransport);
        });
        bobOSRef = bobOS;

        // Note: bobOS intentionally does NOT register RemoteSecretNoteNeuron.
        var noteId = new NeuronId(aliceBrain, "facts.transport.note", "customer-1");
        const string secretText = "secret-customer-data";

        // Alice writes her secret note locally and offers it to Bob as Viewer.
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            await aliceOS.Get<IRemoteSecretNote>(noteId).Write(secretText);
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Offer(noteId, "bob", Role.Viewer, ct);
        }

        // Bob calls Alice's offered neuron from bobOS across the sealed opaque relay.
        using (bobOS.Edge.Enter(bobOS.Edge.Person("bob", "bob", bobBrain)))
        {
            var read = await bobOS.Get<IRemoteSecretNote>(noteId).Read();
            Assert.Equal(secretText, read);

            // Verify the opaque relay captured sealed request & response envelopes and never saw plaintext.
            Assert.Equal(2, capturedEnvelopes.Count);
            var secretBytes = Encoding.UTF8.GetBytes(secretText);
            foreach (var envelope in capturedEnvelopes)
            {
                Assert.NotEmpty(envelope.Ciphertext);
                Assert.NotEmpty(envelope.Signature);
                Assert.NotNull(envelope.EphemeralPublicKey);
                Assert.NotNull(envelope.Nonce);
                Assert.NotNull(envelope.Tag);
                Assert.Equal(-1, envelope.Ciphertext.AsSpan().IndexOf(secretBytes));
                Assert.DoesNotContain(secretText, Encoding.UTF8.GetString(envelope.Ciphertext), StringComparison.Ordinal);
            }

            // Bob's write call must be refused by Alice's AccessGate (offer.read-only).
            var writeError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bobOS.Get<IRemoteSecretNote>(noteId).Write("tamper"));
            Assert.Contains("offer.read-only", writeError.Message, StringComparison.Ordinal);
        }

        // Alice revokes Bob's offer.
        using (aliceOS.Edge.Enter(aliceOS.Edge.Person("alice", "alice", aliceBrain)))
        {
            var offers = aliceOS.Get<IOffers>(new NeuronId(aliceBrain, "component.offers", IOffers.DefaultKey));
            await offers.Revoke(noteId, "bob", ct);
        }

        // Bob's next read call must be refused by Alice's AccessGate (offer.none).
        using (bobOS.Edge.Enter(bobOS.Edge.Person("bob", "bob", bobBrain)))
        {
            var revokedError = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bobOS.Get<IRemoteSecretNote>(noteId).Read());
            Assert.Contains("offer.none", revokedError.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TamperedCiphertextOrWrongSignatureIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        using var attackerKeys = BrainKeyPair.Generate();

        var plaintext = "confidential-payload"u8.ToArray();
        var sealedEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-1",
            plaintext,
            bobKeys,
            aliceKeys.ExportPublicKeys());

        // Sanity check: unmodified envelope opens cleanly.
        var opened = BrainEnvelopeSealer.Open(sealedEnvelope, aliceKeys, bobKeys.ExportPublicKeys());
        Assert.Equal(plaintext, opened);

        // 1. Mutate 1 byte of Ciphertext -> rejected with CryptographicException.
        var tamperedCiphertext = (byte[])sealedEnvelope.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0xFF;
        var tamperedEnvelope = sealedEnvelope with { Ciphertext = tamperedCiphertext };
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(tamperedEnvelope, aliceKeys, bobKeys.ExportPublicKeys()));

        // 2. Signed with wrong key (attacker impersonating Bob) -> rejected with CryptographicException.
        var forgedEnvelope = BrainEnvelopeSealer.Seal(
            bobBrain,
            aliceBrain,
            "msg-2",
            plaintext,
            attackerKeys,
            aliceKeys.ExportPublicKeys());
        Assert.Throws<CryptographicException>(() =>
            BrainEnvelopeSealer.Open(forgedEnvelope, aliceKeys, bobKeys.ExportPublicKeys()));

        // 3. End-to-end: BrainEnvelopeReceiver rejects tampered & forged envelopes before ever reaching the target neuron.
        var targetInvoked = false;
        await using var aliceOS = TestOS.Local();
        var receiver = new BrainEnvelopeReceiver(
            aliceBrain,
            aliceKeys,
            brain => brain == bobBrain ? bobKeys.ExportPublicKeys() : null,
            aliceOS.Edge,
            (target, call, token) =>
            {
                targetInvoked = true;
                return aliceOS.InvokeLocalAsync(target, call, token);
            });

        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(tamperedEnvelope, ct));
        await Assert.ThrowsAsync<CryptographicException>(() => receiver.DeliverAsync(forgedEnvelope, ct));
        Assert.False(targetInvoked);
    }
}
