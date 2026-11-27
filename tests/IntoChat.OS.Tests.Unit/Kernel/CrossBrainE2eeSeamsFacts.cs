using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Components;
using DigitalBrain.Invocation;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Codes;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Identity.Contracts.Codes;
using IntoChat.OS.Modules.Identity.Keys;
using IntoChat.OS.Tests.Support.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Kernel;

/// <summary>
/// End-to-end two-brain tests closing INT-2601 B3 Slice 2 Seams 1, 2, 3, and 5 across two completely
/// isolated <see cref="TestOS"/> clusters communicating ONLY over sealed <see cref="BrainEnvelope"/>s,
/// Cloud friend-code rendezvous (<c>/circle/v1/friends/codes/*</c>), and opaque E2EE media blobs
/// (<c>/circle/v1/brains/blobs</c>), with ZERO shared local <see cref="IBrainOwners"/> entries for remote peers.
/// </summary>
public sealed class CrossBrainE2eeSeamsFacts
{
    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private sealed class LocalOnlyBrainOwners(string localBrainId, string localAccount) : IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken ct = default)
            => Task.FromResult(string.Equals(brain.Value, localBrainId, StringComparison.Ordinal) ? localAccount : null);
    }

    private sealed class PassthroughRedactor : ISecretRedactor
    {
        public ValueTask<string?> Redact(string? text, CancellationToken cancellationToken) => ValueTask.FromResult(text);
        public ValueTask<int> TailLength(CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class InMemoryCodeBook : ICodeBook
    {
        private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
        private readonly ConcurrentDictionary<string, (CodePurpose Purpose, BrainId Brain, string Ref, DateTimeOffset ExpiresAt)> _entries = new(StringComparer.Ordinal);

        public Task<string> Bind(Code code, CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt)
        {
            var hash = code.Hash(_hmacKey);
            _entries[hash] = (purpose, brain, reference, expiresAt);
            return Task.FromResult(hash);
        }

        public Task<string> HashOf(Code code) => Task.FromResult(code.Hash(_hmacKey));

        public Task<CodeTarget?> Find(CodePurpose purpose, string hash)
            => Task.FromResult(_entries.TryGetValue(hash, out var e) && e.Purpose == purpose ? new CodeTarget(e.Brain, e.Ref, e.ExpiresAt) : null);

        public Task<CodeTarget?> Lookup(CodePurpose purpose, string hash) => Find(purpose, hash);

        public Task Spend(string hash)
        {
            _entries.TryRemove(hash, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class MockCloudRelay : IBrainRelayClient, IBrainRelayClientProvider
    {
        private readonly ConcurrentDictionary<string, FriendCodeBundle> _codes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (byte[] Ciphertext, string Sha256, HashSet<string> Recipients)> _blobs = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, BrainEnvelopeReceiver> _receivers = new(StringComparer.Ordinal);

        public Uri RelayBaseUri { get; } = new("https://cloud.intochat.test");
        public bool IsConfigured => true;
        public IBrainRelayClient TryGetClient() => this;

        public List<BrainEnvelope> CapturedEnvelopes { get; } = [];
        public List<string> PublishedCodeHashes { get; } = [];
        public List<string> RevokedCodeHashes { get; } = [];
        public List<(string BlobId, string[] Recipients, string Sha256, int Size)> UploadedBlobs { get; } = [];

        public Func<FriendCodeBundle, FriendCodeBundle>? OnResolveBundleMutator { get; set; }
        public Func<byte[], byte[]>? OnDownloadBlobMutator { get; set; }

        public void RegisterReceiver(BrainId brain, BrainEnvelopeReceiver receiver)
            => _receivers[brain.Value] = receiver;

        public async Task<BrainEnvelope> SendAndWaitReplyAsync(BrainEnvelope envelope, CancellationToken ct = default)
        {
            CapturedEnvelopes.Add(envelope);
            if (!_receivers.TryGetValue(envelope.To.Value, out var receiver))
            {
                throw new InvalidOperationException($"No receiver registered for brain '{envelope.To.Value}'.");
            }
            var reply = await receiver.DeliverAsync(envelope, ct);
            CapturedEnvelopes.Add(reply);
            return reply;
        }

        public async Task SendOneWayAsync(BrainEnvelope envelope, CancellationToken ct = default)
        {
            CapturedEnvelopes.Add(envelope);
            if (_receivers.TryGetValue(envelope.To.Value, out var receiver))
            {
                _ = await receiver.DeliverAsync(envelope, ct);
            }
        }

        public Task AckAsync(string messageId, string? leaseId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> PollOnceAsync(BrainEnvelopeReceiver receiver, CancellationToken ct = default) => Task.FromResult(false);
        public Task RunPollLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct) => Task.CompletedTask;
        public Task RunStreamLoopAsync(BrainEnvelopeReceiver receiver, CancellationToken ct) => Task.CompletedTask;

        public Task PublishFriendCodeAsync(FriendCodeBundle bundle, CancellationToken ct = default)
        {
            _codes[bundle.CodeHash] = bundle;
            PublishedCodeHashes.Add(bundle.CodeHash);
            return Task.CompletedTask;
        }

        public Task<FriendCodeBundle?> ResolveFriendCodeAsync(string codeHash, CancellationToken ct = default)
        {
            if (!_codes.TryGetValue(codeHash, out var bundle))
            {
                return Task.FromResult<FriendCodeBundle?>(null);
            }

            if (OnResolveBundleMutator is not null)
            {
                bundle = OnResolveBundleMutator(bundle);
            }

            return Task.FromResult<FriendCodeBundle?>(bundle);
        }

        public Task RevokeFriendCodeAsync(string codeHash, CancellationToken ct = default)
        {
            _codes.TryRemove(codeHash, out _);
            RevokedCodeHashes.Add(codeHash);
            return Task.CompletedTask;
        }

        public Task<BrainBlobUploadResult> UploadBlobAsync(
            ReadOnlyMemory<byte> ciphertext,
            IReadOnlyList<BrainId> recipients,
            CancellationToken ct = default)
        {
            var bytes = ciphertext.ToArray();
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var blobId = "blob_" + Guid.NewGuid().ToString("N")[..16];
            var recipientIds = recipients.Select(static r => r.Value).ToArray();
            _blobs[blobId] = (bytes, sha, new HashSet<string>(recipientIds, StringComparer.Ordinal));
            UploadedBlobs.Add((blobId, recipientIds, sha, bytes.Length));
            return Task.FromResult(new BrainBlobUploadResult(blobId, sha, bytes.Length, DateTimeOffset.UtcNow.AddDays(7)));
        }

        public Task<BrainBlobDownloadResult?> DownloadBlobAsync(
            string blobId,
            string? expectedSha256 = null,
            CancellationToken ct = default)
        {
            if (!_blobs.TryGetValue(blobId, out var entry))
            {
                return Task.FromResult<BrainBlobDownloadResult?>(null);
            }

            var bytes = OnDownloadBlobMutator is not null ? OnDownloadBlobMutator(entry.Ciphertext) : entry.Ciphertext;
            var actualSha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (expectedSha256 is not null && !string.Equals(expectedSha256, actualSha, StringComparison.Ordinal))
            {
                throw new CryptographicException("Downloaded blob SHA-256 does not match the expected digest.");
            }

            return Task.FromResult<BrainBlobDownloadResult?>(new BrainBlobDownloadResult(bytes, actualSha));
        }
    }

    private sealed class ClusterNode : IAsyncDisposable
    {
        public required BrainId Brain { get; init; }
        public required string Account { get; init; }
        public required TestOS Os { get; init; }
        public required ServiceProvider Services { get; init; }
        public required KernelBrainKeyDirectory KeyDirectory { get; init; }
        public required BrainEnvelopeReceiver Receiver { get; init; }
        public required FileAttachmentStore Attachments { get; init; }
        public required SharedAttachmentRelay SharedAttachments { get; init; }
        public required string TempDir { get; init; }

        public IDisposable AsOwner() => Os.Edge.Enter(Os.Edge.Person(Account, Account, Brain));

        public async ValueTask DisposeAsync()
        {
            await Os.DisposeAsync();
            await Services.DisposeAsync();
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }
        }
    }

    private static ClusterNode CreateCluster(string brainId, string account, MockCloudRelay cloud, TimeProvider clock)
    {
        var brain = new BrainId(brainId);
        var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-e2ee-seams-{brainId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        TestOS? osRef = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddSingleton(_ => osRef!.Edge);
        services.AddSingleton<ICallerAmbient>(new DelegatingCallerAmbient(() => osRef!.Callers));
        services.AddSingleton(_ => osRef!.Proxies);
        services.AddSingleton<IBrainSecretSealer, InMemoryBrainSecretSealer>();
        services.AddSingleton<IBrainKeySigner, IdentityBrainKeySigner>();
        services.AddSingleton<IBrainOwners>(new LocalOnlyBrainOwners(brainId, account));
        services.AddSingleton<ICodeBook, InMemoryCodeBook>();
        services.AddSingleton<ISecretRedactor, PassthroughRedactor>();
        services.AddSingleton<IBrainRelayClient>(cloud);
        services.AddSingleton<IBrainRelayClientProvider>(cloud);
        services.AddSingleton(new FileAttachmentStore(tempDir, clock));
        services.AddSingleton<SharedAttachmentRelay>();
        services.AddSingleton<FriendsFriendCheck>();
        services.AddSingleton<IFriendCheck>(static sp => sp.GetRequiredService<FriendsFriendCheck>());
        services.AddSingleton<IFriendPeerDirectory>(static sp => sp.GetRequiredService<FriendsFriendCheck>());
        services.AddSingleton<KernelBrainKeyDirectory>(sp => new KernelBrainKeyDirectory(sp, sp.GetRequiredService<ICallerAmbient>()));
        services.AddSingleton<IBrainKeyDirectory>(static sp => sp.GetRequiredService<KernelBrainKeyDirectory>());

        SealedRelayBrainTransport? transport = null;
        services.AddSingleton<IBrainTransport>(new DelegatingBrainTransport(() => transport!));

        var provider = services.BuildServiceProvider();
        var signer = provider.GetRequiredService<IBrainKeySigner>();
        var keyDirectory = provider.GetRequiredService<KernelBrainKeyDirectory>();

        var os = osRef = TestOS.Local(options =>
        {
            options.Services = provider;
            options.Start = clock.GetUtcNow();
        });

        os.Register(typeof(IdentityModule).Assembly);
        os.Register<FriendsNeuron>();
        os.Register<ProfileNeuron>();
        os.Register<ThreadDirectoryNeuron>();
        os.Register<ConversationNeuron>();

        var receiver = new BrainEnvelopeReceiver(
            isLocalBrain: b => b == brain,
            signer: signer,
            scopedKeyLookup: keyDirectory.GetPinnedKeysForAsync,
            edge: os.Edge,
            invoke: os.InvokeLocalAsync,
            post: os.PostLocalAsync,
            timeProvider: clock,
            scopedVerifyCallerAccount: keyDirectory.VerifyCallerAccountForAsync,
            allowUnpinnedHandshake: BrainRelayTransportHosting.IsAllowedUnpinnedFriendHandshake);

        transport = new SealedRelayBrainTransport(
            signer: signer,
            keyLookup: (targetBrain, ct) => keyDirectory.GetPinnedKeysForAsync(brain, targetBrain, ct),
            callers: os.Callers,
            relay: cloud.SendAndWaitReplyAsync,
            postRelay: cloud.SendOneWayAsync,
            isRemote: b => !b.IsSystem && b != brain,
            localBrain: brain,
            timeProvider: clock,
            includeSenderPublicKeys: BrainRelayTransportHosting.IsFriendHandshakeCall);

        cloud.RegisterReceiver(brain, receiver);

        return new ClusterNode
        {
            Brain = brain,
            Account = account,
            Os = os,
            Services = provider,
            KeyDirectory = keyDirectory,
            Receiver = receiver,
            Attachments = provider.GetRequiredService<FileAttachmentStore>(),
            SharedAttachments = provider.GetRequiredService<SharedAttachmentRelay>(),
            TempDir = tempDir,
        };
    }

    private sealed class DelegatingBrainTransport(Func<IBrainTransport> resolve) : IBrainTransport
    {
        public bool IsRemote(BrainId brain) => resolve().IsRemote(brain);
        public Task<JsonElement?> InvokeRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
            => resolve().InvokeRemoteAsync(target, call, cancellationToken);
        public Task PostRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
            => resolve().PostRemoteAsync(target, call, cancellationToken);
    }

    private static byte[] CreateMinimalValidWavBytes()
    {
        // Valid 44-byte RIFF/WAVE header + 16 bytes of PCM audio samples.
        var wav = new byte[60];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), wav.Length - 8);
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), 16000);
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), 32000);
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (short)2);
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (short)16);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), 16);
        for (var i = 44; i < wav.Length; i++)
        {
            wav[i] = (byte)(i * 7);
        }
        return wav;
    }

    [Fact]
    public async Task Seam1_FriendsMintRedeemRevokeRoundTripOverCloudRendezvousAndRejectsTamperedOrExpiredBundles()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 16, 0, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();
        await using var alice = CreateCluster("alice", "alice", cloud, clock);
        await using var bob = CreateCluster("bob", "bob", cloud, clock);

        // 1. Alice mints a code -> published as P-256 self-signed bundle keyed by SHA-256 codeHash.
        Code code1;
        using (alice.AsOwner())
        {
            code1 = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }
        var code1Hash = FriendCodeBundleSigner.ComputeCodeHash(code1.Value);
        Assert.Contains(code1Hash, cloud.PublishedCodeHashes);

        // 2. Tampered P-256 signature on resolved bundle is rejected fail-closed!
        cloud.OnResolveBundleMutator = b =>
        {
            var tamperedSig = (byte[])b.SelfSignature.Clone();
            tamperedSig[0] ^= 0xFF;
            return b with { SelfSignature = tamperedSig };
        };
        using (bob.AsOwner())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code1));
        }
        cloud.OnResolveBundleMutator = null;

        // 3. Expired bundle is rejected fail-closed!
        cloud.OnResolveBundleMutator = b => b with { ExpiresAt = clock.GetUtcNow().AddMinutes(-1) };
        using (bob.AsOwner())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code1));
        }
        cloud.OnResolveBundleMutator = null;

        // 4. Alice revokes code1 -> revoked locally AND on Cloud -> Bob cannot redeem.
        using (alice.AsOwner())
        {
            await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Revoke(code1);
        }
        Assert.Contains(code1Hash, cloud.RevokedCodeHashes);
        using (bob.AsOwner())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code1));
        }

        // 5. Alice mints a fresh code -> Bob redeems across completely separate OS clusters (ZERO shared IBrainOwners).
        Code code2;
        using (alice.AsOwner())
        {
            code2 = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }

        Friend bobViewOfAlice;
        using (bob.AsOwner())
        {
            bobViewOfAlice = await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code2);
        }

        // Seam 3: Both sides persisted AccountId + valid P-256 BrainPublicKeys + RelayHint.
        Assert.Equal(new AccountId("alice"), bobViewOfAlice.Account);
        Assert.Equal(new BrainId("alice"), bobViewOfAlice.Brain);
        Assert.NotNull(bobViewOfAlice.PublicKeys);
        BrainPublicKeyValidation.ValidateNistP256(bobViewOfAlice.PublicKeys!);
        Assert.Equal("https://cloud.intochat.test", bobViewOfAlice.RelayHint);

        Friend aliceViewOfBob;
        using (alice.AsOwner())
        {
            aliceViewOfBob = Assert.Single(await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).List());
        }
        Assert.Equal(new AccountId("bob"), aliceViewOfBob.Account);
        Assert.Equal(new BrainId("bob"), aliceViewOfBob.Brain);
        Assert.NotNull(aliceViewOfBob.PublicKeys);
        BrainPublicKeyValidation.ValidateNistP256(aliceViewOfBob.PublicKeys!);
        Assert.Equal("https://cloud.intochat.test", aliceViewOfBob.RelayHint);

        // Zero local IBrainOwners entries for the remote peer on either cluster!
        Assert.Null(await alice.Services.GetRequiredService<IBrainOwners>().OwnerAccount(new BrainId("bob"), ct));
        Assert.Null(await bob.Services.GetRequiredService<IBrainOwners>().OwnerAccount(new BrainId("alice"), ct));

        // Zero plaintext Via or code leaks on Cloud envelopes!
        Assert.NotEmpty(cloud.CapturedEnvelopes);
        foreach (var env in cloud.CapturedEnvelopes)
        {
            var raw = Encoding.UTF8.GetString(env.Ciphertext);
            Assert.DoesNotContain("friends.links", raw, StringComparison.Ordinal);
            Assert.DoesNotContain(code2.Value, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Seam2AndSeam3_TwoSeparateClustersExchangeCrossBrainInvitesMessagesReactionsEditsDeletesAndNudgesWithZero403AndRejectForgedVia()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 16, 30, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();
        await using var alice = CreateCluster("alice", "alice", cloud, clock);
        await using var bob = CreateCluster("bob", "bob", cloud, clock);

        // Establish friendship across the two clusters via Cloud rendezvous (Seam 1 + 2 + 3).
        Code code;
        using (alice.AsOwner())
        {
            code = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }
        using (bob.AsOwner())
        {
            await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code);
        }

        // Alice creates a CrossBrainShared room and invites Bob (cross-cluster ThreadDirectoryNeuron.Invited with cryptographic Via!).
        NeuronId roomId;
        using (alice.AsOwner())
        {
            var dir = alice.Os.Get<IThreadDirectory>(new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            roomId = await dir.CreateShared("Two-PC E2EE Room", ThreadKind.SharedRoom, "room-e2ee-1");
            var conv = alice.Os.Get<IConversation>(roomId);
            await conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Two-PC E2EE Room");
            await conv.Invite(new AccountId("bob"), bob.Brain, Role.Editor);
        }

        // Bob's ThreadDirectoryNeuron received the cross-cluster invite without 403!
        using (bob.AsOwner())
        {
            var bobDir = bob.Os.Get<IThreadDirectory>(new NeuronId(bob.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            var page = await bobDir.List();
            var entry = Assert.Single(page.Entries);
            Assert.Equal(roomId, entry.Thread);
            Assert.Equal(ThreadTrustPosture.CrossBrainShared, entry.TrustPosture);
            Assert.False(entry.IsHost);
        }

        // Bob sends a message, reacts, edits, and deletes across OS-B -> Cloud -> OS-A (nudging Bob's directory on every change!).
        using (bob.AsOwner())
        {
            var remoteConv = bob.Os.Get<IConversation>(roomId);
            await remoteConv.Say("hello from OS-B");
            var view1 = await remoteConv.Read();
            var msg = Assert.Single(view1.Entries.OfType<Said>());
            Assert.Equal("hello from OS-B", msg.Text);

            await remoteConv.React(msg.Id, "🔥");
            await remoteConv.Edit(msg.Id, "edited from OS-B");
            var view2 = await remoteConv.Read();
            var edited = Assert.Single(view2.Entries.OfType<Said>());
            Assert.Equal("edited from OS-B", edited.Text);
            Assert.Equal("🔥", Assert.Single(edited.Reactions!).Emoji);

            await remoteConv.Delete(msg.Id);
            var view3 = await remoteConv.Read();
            Assert.True(Assert.Single(view3.Entries.OfType<Said>()).Deleted);

            // Bob's ThreadDirectoryNeuron received cross-cluster Changed nudges without 403!
            var bobDir = bob.Os.Get<IThreadDirectory>(new NeuronId(bob.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            Assert.True(await bobDir.LastRevision(roomId) >= view3.Revision);
        }

        // Negative Seam 2 tests: forged signature, mismatched target, wrong Via.Brain, and expired ViaProvenance MUST fail closed!
        var bobSigner = bob.Services.GetRequiredService<IBrainKeySigner>();
        var alicePublicKeys = await alice.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(alice.Brain, ct);
        var targetDir = new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey);
        var call = new RuntimeInvocation(
            string.Empty,
            Threads.Type,
            targetDir.ToString(),
            nameof(IThreadDirectory.Changed),
            [JsonSerializer.SerializeToElement(roomId, RuntimeProtocol.Json), JsonSerializer.SerializeToElement(99L, RuntimeProtocol.Json)]);

        // (a) Forged Via signature
        var validVia = await RemoteViaProvenanceSigner.SignAsync(
            new NeuronId(bob.Brain, IConversation.Type, "conversations/forged"),
            bob.Brain,
            targetDir,
            "bob",
            clock.GetUtcNow(),
            clock.GetUtcNow().AddMinutes(2),
            bobSigner,
            ct);
        var forgedSig = (byte[])validVia.Signature.Clone();
        forgedSig[0] ^= 0xFF;
        var forgedReq = new RemoteInvocationRequest(targetDir, call, "bob", "bob", bob.Brain, OneWay: false, ViaProvenance: validVia with { Signature = forgedSig });
        var forgedEnv = await BrainEnvelopeSealer.SealAsync(
            bob.Brain,
            alice.Brain,
            "msg-forged-via-1",
            JsonSerializer.SerializeToUtf8Bytes(forgedReq, RuntimeProtocol.Json),
            bobSigner,
            alicePublicKeys,
            sentAtUtc: clock.GetUtcNow(),
            ct: ct);
        var forgedReply = await alice.Receiver.DeliverAsync(forgedEnv, ct);
        var forgedReplyPlain = await BrainEnvelopeSealer.OpenAsync(forgedReply, bobSigner, alicePublicKeys, ct);
        var forgedResponse = JsonSerializer.Deserialize<RemoteInvocationResponse>(forgedReplyPlain, RuntimeProtocol.Json)!;
        Assert.Equal(nameof(UnauthorizedAccessException), forgedResponse.ErrorType);

        // (b) Expired Via provenance
        var expiredVia = await RemoteViaProvenanceSigner.SignAsync(
            new NeuronId(bob.Brain, IConversation.Type, "conversations/expired"),
            bob.Brain,
            targetDir,
            "bob",
            clock.GetUtcNow().AddMinutes(-5),
            clock.GetUtcNow().AddMinutes(-1),
            bobSigner,
            ct);
        var expiredReq = new RemoteInvocationRequest(targetDir, call, "bob", "bob", bob.Brain, OneWay: false, ViaProvenance: expiredVia);
        var expiredEnv = await BrainEnvelopeSealer.SealAsync(
            bob.Brain,
            alice.Brain,
            "msg-expired-via-1",
            JsonSerializer.SerializeToUtf8Bytes(expiredReq, RuntimeProtocol.Json),
            bobSigner,
            alicePublicKeys,
            sentAtUtc: clock.GetUtcNow(),
            ct: ct);
        var expiredReply = await alice.Receiver.DeliverAsync(expiredEnv, ct);
        var expiredReplyPlain = await BrainEnvelopeSealer.OpenAsync(expiredReply, bobSigner, alicePublicKeys, ct);
        var expiredResponse = JsonSerializer.Deserialize<RemoteInvocationResponse>(expiredReplyPlain, RuntimeProtocol.Json)!;
        Assert.Equal(nameof(UnauthorizedAccessException), expiredResponse.ErrorType);

        // (c) Mismatched target in Via provenance
        var wrongTargetVia = await RemoteViaProvenanceSigner.SignAsync(
            new NeuronId(bob.Brain, IConversation.Type, "conversations/wrong-target"),
            bob.Brain,
            FriendsDefaults.Id("alice"), // signed for FriendsNeuron, replayed to ThreadDirectoryNeuron!
            "bob",
            clock.GetUtcNow(),
            clock.GetUtcNow().AddMinutes(2),
            bobSigner,
            ct);
        var mismatchReq = new RemoteInvocationRequest(targetDir, call, "bob", "bob", bob.Brain, OneWay: false, ViaProvenance: wrongTargetVia);
        var mismatchEnv = await BrainEnvelopeSealer.SealAsync(
            bob.Brain,
            alice.Brain,
            "msg-mismatch-via-1",
            JsonSerializer.SerializeToUtf8Bytes(mismatchReq, RuntimeProtocol.Json),
            bobSigner,
            alicePublicKeys,
            sentAtUtc: clock.GetUtcNow(),
            ct: ct);
        var mismatchReply = await alice.Receiver.DeliverAsync(mismatchEnv, ct);
        var mismatchReplyPlain = await BrainEnvelopeSealer.OpenAsync(mismatchReply, bobSigner, alicePublicKeys, ct);
        var mismatchResponse = JsonSerializer.Deserialize<RemoteInvocationResponse>(mismatchReplyPlain, RuntimeProtocol.Json)!;
        Assert.Equal(nameof(UnauthorizedAccessException), mismatchResponse.ErrorType);
    }

    [Fact]
    public async Task Seam5_TwoBrainE2eeVoiceClipCardAndAttachmentRoundTripAndNegativeSecurityChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 17, 0, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();
        await using var alice = CreateCluster("alice", "alice", cloud, clock);
        await using var bob = CreateCluster("bob", "bob", cloud, clock);
        await using var eve = CreateCluster("eve", "eve", cloud, clock);

        // 1. Alice and Bob become friends over Cloud rendezvous & Alice opens a CrossBrainShared thread.
        Code code;
        using (alice.AsOwner())
        {
            code = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }
        Friend bobFriendAlice;
        using (bob.AsOwner())
        {
            bobFriendAlice = await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code);
        }

        NeuronId roomId;
        using (alice.AsOwner())
        {
            var dir = alice.Os.Get<IThreadDirectory>(new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            roomId = await dir.CreateShared("Voice Notes Room", ThreadKind.SharedRoom, "room-voice-1");
            var conv = alice.Os.Get<IConversation>(roomId);
            await conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Voice Notes Room");
            await conv.Invite(new AccountId("bob"), bob.Brain, Role.Editor);
        }

        // 2. Bob records a playable WAV VoiceClipCard voice note on OS-B, seals it via BrainBlobSealer + /circle/v1/brains/blobs,
        //    and sends it inside the CrossBrainShared ConversationNeuron on OS-A.
        var wavBytes = CreateMinimalValidWavBytes();
        var validatedWav = AttachmentValidator.ValidateUpload("note.wav", "audio/wav", wavBytes, requireVoiceNote: true, durationMs: 4200);
        var bobLocalVoice = await bob.Attachments.SaveAsync(bob.Brain, null, validatedWav, wavBytes, "Meet at noon", ct);
        var bobPublicKeys = await bob.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(bob.Brain, ct);

        var sealedVoiceRef = await bob.SharedAttachments.SealAndUploadAsync(
            bob.Brain,
            bobLocalVoice,
            [(alice.Brain, bobFriendAlice.PublicKeys!), (bob.Brain, bobPublicKeys)],
            ct);

        Assert.NotNull(sealedVoiceRef.BlobId);
        Assert.NotNull(sealedVoiceRef.BlobSha256);
        Assert.Equal("bob", sealedVoiceRef.SenderBrainId);

        using (bob.AsOwner())
        {
            await bob.Os.Get<IConversation>(roomId).Say(string.Empty, attachments: [sealedVoiceRef]);
        }

        // Verify Alice's ConversationNeuron unsealed, verified, and surfaced the playable VoiceClipCard attachment!
        using (alice.AsOwner())
        {
            var conv = alice.Os.Get<IConversation>(roomId);
            var view = await conv.Read();
            Assert.Equal(ThreadTrustPosture.CrossBrainShared, view.TrustPosture);
            var said = Assert.Single(view.Entries.OfType<Said>());
            var voiceCard = Assert.Single(said.Attachments!);
            Assert.Equal(AttachmentValidator.KindVoiceNote, voiceCard.Kind);
            Assert.Equal("audio/wav", voiceCard.MediaType);
            Assert.Equal(4200, voiceCard.DurationMs);
            Assert.Equal("Meet at noon", voiceCard.Transcript);
            Assert.Equal(sealedVoiceRef.BlobId, voiceCard.BlobId);
            Assert.Equal("bob", voiceCard.SenderBrainId);

            // Exact playable WAV audio bytes are materialized in Alice's local FileAttachmentStore.
            var storedOnAlice = await alice.Attachments.TryReadAsync(alice.Brain, voiceCard.Id, ct);
            Assert.NotNull(storedOnAlice);
            Assert.Equal(wavBytes, storedOnAlice!.Bytes);

            // Trust posture remains strictly immutable!
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant));
        }

        // 3. Host (Alice) sends an attachment -> ConversationNeuron seals & uploads it for [alice, bob] -> Bob downloads & unseals!
        var noteBytes = "shared-room-notes-plaintext"u8.ToArray();
        var validatedTxt = AttachmentValidator.ValidateUpload("agenda.txt", "text/plain", noteBytes);
        var aliceLocalDoc = await alice.Attachments.SaveAsync(alice.Brain, null, validatedTxt, noteBytes, null, ct);

        using (alice.AsOwner())
        {
            await alice.Os.Get<IConversation>(roomId).Say("Agenda attached", attachments: [aliceLocalDoc]);
        }

        AttachmentRef aliceSharedDoc;
        using (bob.AsOwner())
        {
            var view = await bob.Os.Get<IConversation>(roomId).Read();
            var agendaEntry = view.Entries.OfType<Said>().Single(s => s.Text == "Agenda attached");
            aliceSharedDoc = Assert.Single(agendaEntry.Attachments!);
            Assert.NotNull(aliceSharedDoc.BlobId);
            Assert.NotNull(aliceSharedDoc.BlobSha256);
            Assert.Equal("alice", aliceSharedDoc.SenderBrainId);

            var (_, decrypted) = await bob.SharedAttachments.DownloadAndUnsealAsync(
                bob.Brain,
                aliceSharedDoc,
                expectedSenderKeys: bobFriendAlice.PublicKeys,
                persistToLocalStore: false,
                ct: ct);
            Assert.Equal(noteBytes, decrypted);
        }

        // 4. Negative security checks:
        // (a) Non-member recipient (Eve) cannot unseal the blob!
        await Assert.ThrowsAsync<CryptographicException>(() =>
            eve.SharedAttachments.DownloadAndUnsealAsync(
                eve.Brain,
                aliceSharedDoc,
                expectedSenderKeys: bobFriendAlice.PublicKeys,
                persistToLocalStore: false,
                ct: ct));

        // (b) Wrong sender public key fails signature verification!
        var evePublicKeys = await eve.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(eve.Brain, ct);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            bob.SharedAttachments.DownloadAndUnsealAsync(
                bob.Brain,
                aliceSharedDoc,
                expectedSenderKeys: evePublicKeys,
                persistToLocalStore: false,
                ct: ct));

        // (c) Tampered ciphertext fails SHA-256 / integrity verification!
        cloud.OnDownloadBlobMutator = bytes =>
        {
            var copy = (byte[])bytes.Clone();
            copy[^1] ^= 0xFF;
            return copy;
        };
        await Assert.ThrowsAsync<CryptographicException>(() =>
            bob.SharedAttachments.DownloadAndUnsealAsync(
                bob.Brain,
                aliceSharedDoc,
                expectedSenderKeys: bobFriendAlice.PublicKeys,
                persistToLocalStore: false,
                ct: ct));
        cloud.OnDownloadBlobMutator = null;

        // (d) Foreign caller attempting to attach owner brain's local attachment without E2EE blob ref is rejected fail-closed!
        using (bob.AsOwner())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IConversation>(roomId).Say("steal", attachments: [aliceLocalDoc]));
        }
    }

    [Fact]
    public async Task B1_UnpinnedHandshakeRejectsSwappedExchangeKeyAndMismatchedOrNullArgPublicKeysWithoutPollutingReplayCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();
        await using var alice = CreateCluster("alice", "alice", cloud, clock);
        await using var bob = CreateCluster("bob", "bob", cloud, clock);

        Code code;
        using (alice.AsOwner())
        {
            code = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }
        var cloudHash = FriendCodeBundleSigner.ComputeCodeHash(code.Value);

        var aliceSigner = alice.Services.GetRequiredService<IBrainKeySigner>();
        var alicePublicKeys = await aliceSigner.GetPublicKeysAsync(alice.Brain, ct);
        var bobSigner = bob.Services.GetRequiredService<IBrainKeySigner>();
        var bobPublicKeys = await bobSigner.GetPublicKeysAsync(bob.Brain, ct);
        using var rogueKeyPair = BrainKeyPair.Generate();
        var rogueKeys = rogueKeyPair.ExportPublicKeys();

        var targetFriends = FriendsDefaults.Id("alice");
        var bobVia = FriendsDefaults.Id("bob");
        var viaProof = await RemoteViaProvenanceSigner.SignAsync(
            bobVia,
            bob.Brain,
            targetFriends,
            "bob",
            clock.GetUtcNow(),
            clock.GetUtcNow().AddMinutes(2),
            bobSigner,
            ct);

        async Task<BrainEnvelope> BuildHandshakeEnvelopeAsync(string messageId, BrainPublicKeys? argKeys, BrainPublicKeys envelopeSenderKeys)
        {
            var call = new RuntimeInvocation(
                string.Empty,
                "friends",
                targetFriends.ToString(),
                nameof(IFriends.Accepted),
                [
                    JsonSerializer.SerializeToElement(cloudHash, RuntimeProtocol.Json),
                    JsonSerializer.SerializeToElement("Bob", RuntimeProtocol.Json),
                    JsonSerializer.SerializeToElement(argKeys, RuntimeProtocol.Json),
                    JsonSerializer.SerializeToElement("https://cloud.intochat.test", RuntimeProtocol.Json),
                ]);
            var req = new RemoteInvocationRequest(targetFriends, call, "bob", "bob", bob.Brain, OneWay: false, ViaProvenance: viaProof);
            var env = await BrainEnvelopeSealer.SealAsync(
                bob.Brain,
                alice.Brain,
                messageId,
                JsonSerializer.SerializeToUtf8Bytes(req, RuntimeProtocol.Json),
                bobSigner,
                alicePublicKeys,
                sentAtUtc: clock.GetUtcNow(),
                ct: ct);
            return env with { SenderPublicKeys = envelopeSenderKeys };
        }

        // (a) Relay swaps envelope.SenderPublicKeys.ExchangeKey in transit -> MUST fail closed with CryptographicException!
        var swappedExchangeEnvelope = await BuildHandshakeEnvelopeAsync(
            "msg-b1-swap-exchange",
            argKeys: bobPublicKeys,
            envelopeSenderKeys: new BrainPublicKeys(bobPublicKeys.SigningKey, rogueKeys.ExchangeKey));
        await Assert.ThrowsAsync<CryptographicException>(() => alice.Receiver.DeliverAsync(swappedExchangeEnvelope, ct));

        // (b) Passing null publicKeys to IFriends.Accepted -> MUST fail closed with CryptographicException!
        var nullArgEnvelope = await BuildHandshakeEnvelopeAsync(
            "msg-b1-null-arg",
            argKeys: null,
            envelopeSenderKeys: bobPublicKeys);
        await Assert.ThrowsAsync<CryptographicException>(() => alice.Receiver.DeliverAsync(nullArgEnvelope, ct));

        // (c) Passing mismatched publicKeys to IFriends.Accepted -> MUST fail closed with CryptographicException!
        const string reusedMessageId = "msg-b1-mismatched-arg";
        var mismatchedArgEnvelope = await BuildHandshakeEnvelopeAsync(
            reusedMessageId,
            argKeys: rogueKeys,
            envelopeSenderKeys: bobPublicKeys);
        await Assert.ThrowsAsync<CryptographicException>(() => alice.Receiver.DeliverAsync(mismatchedArgEnvelope, ct));

        // (d) Rejected unpinned handshake MUST NOT have polluted Alice's replay cache: a legitimate handshake
        //     with the same MessageId still succeeds!
        var validEnvelope = await BuildHandshakeEnvelopeAsync(
            reusedMessageId,
            argKeys: bobPublicKeys,
            envelopeSenderKeys: bobPublicKeys);
        var reply = await alice.Receiver.DeliverAsync(validEnvelope, ct);
        var replyPlain = await BrainEnvelopeSealer.OpenAsync(reply, bobSigner, alicePublicKeys, ct);
        var response = JsonSerializer.Deserialize<RemoteInvocationResponse>(replyPlain, RuntimeProtocol.Json)!;
        Assert.Null(response.ErrorType);
        Assert.NotNull(response.Result);
    }

    [Fact]
    public async Task B2_DownloadSharedBlobEndpointEnforcesExplicitRecipientBrainScopeWhenAmbientCallerAndLocalBrainIdAreNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 18, 30, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();

        var aliceBrain = new BrainId("alice");
        var bobBrain = new BrainId("bob");
        var eveBrain = new BrainId("eve");

        using var aliceKeys = BrainKeyPair.Generate();
        using var bobKeys = BrainKeyPair.Generate();
        using var eveKeys = BrainKeyPair.Generate();

        var multiBrainSigner = new MultiBrainKeySigner(new Dictionary<string, BrainKeyPair>(StringComparer.Ordinal)
        {
            [aliceBrain.Value] = aliceKeys,
            [bobBrain.Value] = bobKeys,
            [eveBrain.Value] = eveKeys,
        });

        // Multi-brain KernelBrainKeyDirectory with callers == null AND options == null (LocalBrainId == null).
        await using var multiOs = TestOS.Local(o => o.Start = clock.GetUtcNow());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IBrainRelayClient>(cloud);
        services.AddSingleton<IBrainRelayClientProvider>(cloud);
        services.AddSingleton<IBrainKeySigner>(multiBrainSigner);
        services.AddSingleton(multiOs.Edge);
        services.AddSingleton<KernelBrainKeyDirectory>(sp => new KernelBrainKeyDirectory(sp, callers: null, options: null));
        services.AddSingleton<IBrainKeyDirectory>(static sp => sp.GetRequiredService<KernelBrainKeyDirectory>());
        services.AddSingleton<SharedAttachmentRelay>();
        await using var provider = services.BuildServiceProvider();

        var keyDir = provider.GetRequiredService<KernelBrainKeyDirectory>();
        var sharedRelay = provider.GetRequiredService<SharedAttachmentRelay>();

        // Pin Bob's keys ONLY in Alice's scope (NOT in Eve's scope, NOT in DefaultScopeKey "*").
        await keyDir.PinForAsync(aliceBrain, bobBrain, bobKeys.ExportPublicKeys(), ct);

        // Bob seals a playable WAV voice note for [aliceBrain, bobBrain] and uploads to Cloud blob store.
        var wavBytes = CreateMinimalValidWavBytes();
        var sealedBytes = await BrainBlobSealer.SealAsync(
            bobBrain,
            multiBrainSigner,
            wavBytes,
            [(aliceBrain, aliceKeys.ExportPublicKeys()), (bobBrain, bobKeys.ExportPublicKeys())],
            ct);
        var uploaded = await cloud.UploadBlobAsync(sealedBytes, [aliceBrain, bobBrain], ct);
        var contentSha = Convert.ToHexStringLower(SHA256.HashData(wavBytes));

        DefaultHttpContext CreateRequestFor(string account, BrainId brain)
        {
            var reqServices = new ServiceCollection();
            reqServices.AddSingleton<ICallerAmbient>(new FixedCallerAmbient(multiOs.Edge.Person(account, account, brain)));
            reqServices.AddSingleton(multiOs.Edge);
            var http = new DefaultHttpContext { RequestServices = reqServices.BuildServiceProvider() };
            http.Request.QueryString = new QueryString(
                $"?sender={Uri.EscapeDataString(bobBrain.Value)}&sha256={uploaded.Sha256}&fileName=voice.wav&mediaType=audio/wav&kind=voice-note&contentSha256={contentSha}");
            return http;
        }

        // Ambient caller on multiOs is null; LocalBrainId is null.
        Assert.Null(multiOs.Callers.Current);

        // Alice unseals Bob's blob -> 200 OK with exact WAV audio bytes!
        var aliceHttp = CreateRequestFor("alice", aliceBrain);
        var aliceResult = await AttachmentEndpoints.DownloadSharedBlobAsync(aliceHttp, uploaded.BlobId, sharedRelay, ct);
        var fileResult = Assert.IsType<FileContentHttpResult>(aliceResult);
        Assert.Equal("audio/wav", fileResult.ContentType);
        Assert.Equal(wavBytes, fileResult.FileContents.ToArray());

        // Eve has neither Alice's pin nor recipient entry -> 404 Not Found!
        var eveHttp = CreateRequestFor("eve", eveBrain);
        var eveResult = await AttachmentEndpoints.DownloadSharedBlobAsync(eveHttp, uploaded.BlobId, sharedRelay, ct);
        Assert.IsType<NotFound>(eveResult);
    }

    [Fact]
    public async Task B3_RejectedRedeemWithForgedSelfSignedBundleDoesNotPoisonKeyDirectoryAgainstRealPeer()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 19, 0, 0, TimeSpan.Zero));
        var cloud = new MockCloudRelay();
        await using var alice = CreateCluster("alice", "alice", cloud, clock);
        await using var bob = CreateCluster("bob", "bob", cloud, clock);

        // 1. Attacker publishes a valid self-signed FriendCodeBundle claiming BrainId = "alice" using rogue keys.
        var forgedCode = Code.New();
        var forgedHash = FriendCodeBundleSigner.ComputeCodeHash(forgedCode.Value);
        using var rogueKeys = BrainKeyPair.Generate();
        var forgedBundle = FriendCodeBundleSigner.Sign(
            forgedHash,
            alice.Brain,
            rogueKeys,
            clock.GetUtcNow().AddHours(1),
            relayOrigin: "https://cloud.intochat.test");
        await cloud.PublishFriendCodeAsync(forgedBundle, ct);

        // 2. Bob attempts to redeem the forged code -> real Alice rejects decryption/handshake -> Redeem fails.
        using (bob.AsOwner())
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(forgedCode));
        }

        // 3. Bob's IBrainKeyDirectory MUST NOT be poisoned with rogueKeys!
        var pinnedAfterFailure = await bob.KeyDirectory.GetPinnedKeysForAsync(bob.Brain, alice.Brain, ct);
        Assert.Null(pinnedAfterFailure);

        // 4. Real Alice mints a legitimate code -> Bob redeems it cleanly and pins Alice's real keys!
        Code realCode;
        using (alice.AsOwner())
        {
            realCode = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }

        Friend added;
        using (bob.AsOwner())
        {
            added = await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(realCode);
        }

        var realAliceKeys = await alice.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(alice.Brain, ct);
        Assert.NotNull(added.PublicKeys);
        Assert.True(realAliceKeys.Matches(added.PublicKeys));

        var pinnedAfterSuccess = await bob.KeyDirectory.GetPinnedKeysForAsync(bob.Brain, alice.Brain, ct);
        Assert.NotNull(pinnedAfterSuccess);
        Assert.True(realAliceKeys.Matches(pinnedAfterSuccess));
    }

    private sealed class FixedCallerAmbient(Caller caller) : ICallerAmbient
    {
        public Caller? Current => caller;
    }

    private sealed class MultiBrainKeySigner(IReadOnlyDictionary<string, BrainKeyPair> pairs) : IBrainKeySigner
    {
        public ValueTask<BrainPublicKeys> GetPublicKeysAsync(BrainId brainId, CancellationToken ct = default)
            => ValueTask.FromResult(pairs[brainId.Value].ExportPublicKeys());

        public ValueTask<byte[]> SignDataAsync(BrainId brainId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => ValueTask.FromResult(pairs[brainId.Value].SigningKey.SignData(data.Span, HashAlgorithmName.SHA256));

        public ValueTask<byte[]> DeriveSharedSecretAsync(BrainId brainId, ReadOnlyMemory<byte> peerEphemeralPublicKeySpki, CancellationToken ct = default)
        {
            using var peer = ECDiffieHellman.Create();
            peer.ImportSubjectPublicKeyInfo(peerEphemeralPublicKeySpki.Span, out _);
            return ValueTask.FromResult(pairs[brainId.Value].ExchangeKey.DeriveRawSecretAgreement(peer.PublicKey));
        }
    }
}
