using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
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
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Attachments;
using IntoChat.OS.Modules.AI.Conversations;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
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
using Xunit;

namespace IntoChat.OS.Kernel.Tests.E2E;

/// <summary>
/// True Two-Cluster <c>OS-Alice &lt;-&gt; Cloud Relay (Kestrel HTTP+WebSocket) &lt;-&gt; OS-Bob</c> (+ unauthorized <c>OS-Eve</c>)
/// E2EE Messenger E2E suite (INT-2606 / AB-QA Part 1).
/// Uses real <see cref="HttpBrainRelayClient"/>, real <see cref="SealedRelayBrainTransport"/>, real <see cref="BrainEnvelopeReceiver"/>,
/// real <see cref="BrainRelayTransportHosting.IsFriendHandshakeCall"/> / <see cref="BrainRelayTransportHosting.IsAllowedUnpinnedFriendHandshake"/>,
/// real <see cref="KernelBrainKeyDirectory"/>, real <see cref="BrainBlobSealer"/> / <see cref="SharedAttachmentRelay"/>,
/// real <see cref="AttachmentEndpoints.DownloadSharedBlobAsync"/>, and ZERO shared local <see cref="IBrainOwners"/> across clusters.
/// </summary>
public sealed class TwoClusterCrossCloudMessengerE2eFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "TwoClusterMessengerE2E")]
    public async Task Test01_CrossCloudFriendRendezvousAndMutualTofuPinningOverRealHttpAndWebSocket()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        await using var alice = await IsolatedOsCluster.StartAsync("alice", "alice", relay, Ct);
        await using var bob = await IsolatedOsCluster.StartAsync("bob", "bob", relay, Ct);

        // 1. OS-Alice mints a friend code -> published to real Kestrel POST /circle/v1/friends/codes keyed by SHA-256 codeHash.
        Code friendCode;
        using (alice.AsOwner())
        {
            friendCode = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }

        var expectedHash = FriendCodeBundleSigner.ComputeCodeHash(friendCode.Value);
        Assert.Contains(expectedHash, relay.PublishedCodeHashes);

        // 2. OS-Bob redeems over real HTTP + WebSocket -> mutual TOFU P-256 pinning across completely isolated clusters.
        Friend bobViewOfAlice;
        using (bob.AsOwner())
        {
            bobViewOfAlice = await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(friendCode);
        }

        Assert.Equal(new AccountId("alice"), bobViewOfAlice.Account);
        Assert.Equal(alice.Brain, bobViewOfAlice.Brain);
        Assert.NotNull(bobViewOfAlice.PublicKeys);
        BrainPublicKeyValidation.ValidateNistP256(bobViewOfAlice.PublicKeys!);

        Friend aliceViewOfBob;
        using (alice.AsOwner())
        {
            aliceViewOfBob = Assert.Single(await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).List());
        }

        Assert.Equal(new AccountId("bob"), aliceViewOfBob.Account);
        Assert.Equal(bob.Brain, aliceViewOfBob.Brain);
        Assert.NotNull(aliceViewOfBob.PublicKeys);
        BrainPublicKeyValidation.ValidateNistP256(aliceViewOfBob.PublicKeys!);

        // Mutual P-256 keys are pinned in KernelBrainKeyDirectory on both clusters.
        var alicePinnedBob = await alice.KeyDirectory.GetPinnedKeysForAsync(alice.Brain, bob.Brain, Ct);
        var bobPinnedAlice = await bob.KeyDirectory.GetPinnedKeysForAsync(bob.Brain, alice.Brain, Ct);
        Assert.NotNull(alicePinnedBob);
        Assert.NotNull(bobPinnedAlice);

        var actualAliceKeys = await alice.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(alice.Brain, Ct);
        var actualBobKeys = await bob.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(bob.Brain, Ct);
        Assert.True(actualBobKeys.Matches(alicePinnedBob));
        Assert.True(actualAliceKeys.Matches(bobPinnedAlice));

        // Zero shared local IBrainOwners entries across clusters!
        Assert.Null(await alice.Services.GetRequiredService<IBrainOwners>().OwnerAccount(bob.Brain, Ct));
        Assert.Null(await bob.Services.GetRequiredService<IBrainOwners>().OwnerAccount(alice.Brain, Ct));

        // Zero plaintext raw friend code or @handle on the wire (HTTP bodies + WS frames).
        Assert.NotEmpty(relay.CapturedHttpBodies);
        Assert.NotEmpty(relay.CapturedWebSocketFrames);
        foreach (var wireText in relay.CapturedHttpBodies.Concat(relay.CapturedWebSocketFrames))
        {
            Assert.DoesNotContain(friendCode.Value, wireText, StringComparison.Ordinal);
            Assert.DoesNotContain("@alice", wireText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@bob", wireText, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Category", "TwoClusterMessengerE2E")]
    public async Task Test02_RealTimeE2eeDirectMessageReactionsEditsDeletesAndLiveNudgesOverWebSocketStream()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        await using var alice = await IsolatedOsCluster.StartAsync("alice", "alice", relay, Ct);
        await using var bob = await IsolatedOsCluster.StartAsync("bob", "bob", relay, Ct);

        // Pair Alice & Bob via Cloud rendezvous.
        Code friendCode;
        using (alice.AsOwner())
        {
            friendCode = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }

        using (bob.AsOwner())
        {
            await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(friendCode);
        }

        // Alice creates a CrossBrainShared 1:1 DM thread and invites Bob (cryptographic RemoteViaProvenance inside E2EE ciphertext).
        NeuronId dmId;
        using (alice.AsOwner())
        {
            var aliceDir = alice.Os.Get<IThreadDirectory>(new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            dmId = await aliceDir.CreateShared("Alice & Bob DM", ThreadKind.DirectPerson, "dm-e2ee-1");
            var conv = alice.Os.Get<IConversation>(dmId);
            await conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.DirectPerson, "Alice & Bob DM");
            await conv.Invite(new AccountId("bob"), bob.Brain, Role.Editor);
        }

        // Bob's ThreadDirectoryNeuron received the cross-cluster invite over real WebSocket.
        using (bob.AsOwner())
        {
            var bobDir = bob.Os.Get<IThreadDirectory>(new NeuronId(bob.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            var page = await bobDir.List();
            var entry = Assert.Single(page.Entries);
            Assert.Equal(dmId, entry.Thread);
            Assert.Equal(ThreadTrustPosture.CrossBrainShared, entry.TrustPosture);
            Assert.False(entry.IsHost);
        }

        const string secretSay = "super-secret-e2ee-message-from-bob-9f8a";
        const string secretEdit = "super-secret-edited-message-from-bob-3c2d";

        // Bob sends message, reacts, edits, and deletes over real WebSocket/HTTP relay and receives live ThreadDirectoryNeuron.Changed nudges.
        using (bob.AsOwner())
        {
            var remoteConv = bob.Os.Get<IConversation>(dmId);
            await remoteConv.Say(secretSay);
            var view1 = await remoteConv.Read();
            var msg = Assert.Single(view1.Entries.OfType<Said>());
            Assert.Equal(secretSay, msg.Text);

            await remoteConv.React(msg.Id, "🔥");
            await remoteConv.Edit(msg.Id, secretEdit);
            var view2 = await remoteConv.Read();
            var edited = Assert.Single(view2.Entries.OfType<Said>());
            Assert.Equal(secretEdit, edited.Text);
            Assert.Equal("🔥", Assert.Single(edited.Reactions!).Emoji);

            await remoteConv.Delete(msg.Id);
            var view3 = await remoteConv.Read();
            Assert.True(Assert.Single(view3.Entries.OfType<Said>()).Deleted);

            // Wait briefly for the asynchronous one-way ThreadDirectoryNeuron.Changed nudge over WebSocket.
            var bobDir = bob.Os.Get<IThreadDirectory>(new NeuronId(bob.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && await bobDir.LastRevision(dmId) < view3.Revision)
            {
                await Task.Delay(25, Ct);
            }

            Assert.True(await bobDir.LastRevision(dmId) >= view3.Revision);
        }

        // Verify real WebSocket stream frames (auth-ok, deliver, reply/ack) were used.
        var wsFrames = relay.CapturedWebSocketFrames.ToArray();
        Assert.Contains(wsFrames, f => f.Contains("\"auth-ok\"", StringComparison.Ordinal));
        Assert.Contains(wsFrames, f => f.Contains("\"deliver\"", StringComparison.Ordinal));
        Assert.Contains(wsFrames, f => f.Contains("\"reply\"", StringComparison.Ordinal));

        // Zero plaintext message content, Via neuron IDs, or friend codes in all captured wire envelopes/HTTP/WS frames!
        Assert.NotEmpty(relay.CapturedEnvelopes);
        foreach (var wireText in relay.CapturedHttpBodies.Concat(wsFrames))
        {
            Assert.DoesNotContain(secretSay, wireText, StringComparison.Ordinal);
            Assert.DoesNotContain(secretEdit, wireText, StringComparison.Ordinal);
            Assert.DoesNotContain(friendCode.Value, wireText, StringComparison.Ordinal);
            Assert.DoesNotContain("friends.links", wireText, StringComparison.Ordinal);
            Assert.DoesNotContain("conversations/dm-e2ee-1", wireText, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("Category", "TwoClusterMessengerE2E")]
    public async Task Test03_E2eeAttachmentAndPlayableVoiceClipCardOverRelayBlobsWithRecipientAclAndEveDenial()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        await using var alice = await IsolatedOsCluster.StartAsync("alice", "alice", relay, Ct);
        await using var bob = await IsolatedOsCluster.StartAsync("bob", "bob", relay, Ct);
        await using var eve = await IsolatedOsCluster.StartAsync("eve", "eve", relay, Ct);

        // Pair Alice & Bob and open a CrossBrainShared room.
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
            roomId = await dir.CreateShared("Voice & Docs Room", ThreadKind.SharedRoom, "room-blobs-1");
            var conv = alice.Os.Get<IConversation>(roomId);
            await conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Voice & Docs Room");
            await conv.Invite(new AccountId("bob"), bob.Brain, Role.Editor);
        }

        // 1. OS-Bob seals a playable WAV VoiceClipCard over real HTTP POST /circle/v1/brains/blobs.
        var wavBytes = CreateMinimalValidWavBytes();
        var validatedWav = AttachmentValidator.ValidateUpload("voice-note.wav", "audio/wav", wavBytes, requireVoiceNote: true, durationMs: 3600);
        var bobLocalVoice = await bob.Attachments.SaveAsync(bob.Brain, null, validatedWav, wavBytes, "Standup at 10am", Ct);
        var bobPublicKeys = await bob.Services.GetRequiredService<IBrainKeySigner>().GetPublicKeysAsync(bob.Brain, Ct);

        var sealedVoiceRef = await bob.SharedAttachments.SealAndUploadAsync(
            bob.Brain,
            bobLocalVoice,
            [(alice.Brain, bobFriendAlice.PublicKeys!), (bob.Brain, bobPublicKeys)],
            Ct);

        Assert.NotNull(sealedVoiceRef.BlobId);
        Assert.NotNull(sealedVoiceRef.BlobSha256);
        Assert.Equal("bob", sealedVoiceRef.SenderBrainId);

        using (bob.AsOwner())
        {
            await bob.Os.Get<IConversation>(roomId).Say(string.Empty, attachments: [sealedVoiceRef]);
        }

        // OS-Alice unseals & surfaces the playable VoiceClipCard with exact WAV bytes.
        using (alice.AsOwner())
        {
            var view = await alice.Os.Get<IConversation>(roomId).Read();
            var said = Assert.Single(view.Entries.OfType<Said>());
            var voiceCard = Assert.Single(said.Attachments!);
            Assert.Equal(AttachmentValidator.KindVoiceNote, voiceCard.Kind);
            Assert.Equal("audio/wav", voiceCard.MediaType);
            Assert.Equal(3600, voiceCard.DurationMs);
            Assert.Equal("Standup at 10am", voiceCard.Transcript);

            var storedOnAlice = await alice.Attachments.TryReadAsync(alice.Brain, voiceCard.Id, Ct);
            Assert.NotNull(storedOnAlice);
            Assert.Equal(wavBytes, storedOnAlice!.Bytes);
        }

        // Verify AttachmentEndpoints.DownloadSharedBlobAsync with explicit multi-brain recipient scoping for Alice & Eve!
        var wavContentSha = Convert.ToHexStringLower(SHA256.HashData(wavBytes));
        var aliceDownloadHttp = CreateSharedBlobDownloadContext(alice, "alice", alice.Brain, bob.Brain, sealedVoiceRef.BlobSha256!, wavContentSha, "voice-note.wav", "audio/wav", AttachmentValidator.KindVoiceNote);
        var aliceEndpointResult = await AttachmentEndpoints.DownloadSharedBlobAsync(aliceDownloadHttp, sealedVoiceRef.BlobId!, alice.SharedAttachments, Ct);
        var aliceFileResult = Assert.IsType<FileContentHttpResult>(aliceEndpointResult);
        Assert.Equal("audio/wav", aliceFileResult.ContentType);
        Assert.Equal(wavBytes, aliceFileResult.FileContents.ToArray());

        var eveDownloadHttp = CreateSharedBlobDownloadContext(eve, "eve", eve.Brain, bob.Brain, sealedVoiceRef.BlobSha256!, wavContentSha, "voice-note.wav", "audio/wav", AttachmentValidator.KindVoiceNote);
        var eveEndpointResult = await AttachmentEndpoints.DownloadSharedBlobAsync(eveDownloadHttp, sealedVoiceRef.BlobId!, eve.SharedAttachments, Ct);
        Assert.IsType<NotFound>(eveEndpointResult);

        // 2. OS-Alice seals a document attachment -> OS-Bob downloads & unseals exact bytes over real HTTP relay.
        const string docPlaintext = "confidential-quarterly-plan-plaintext-8841";
        var docBytes = Encoding.UTF8.GetBytes(docPlaintext);
        var validatedDoc = AttachmentValidator.ValidateUpload("plan.txt", "text/plain", docBytes);
        var aliceLocalDoc = await alice.Attachments.SaveAsync(alice.Brain, null, validatedDoc, docBytes, null, Ct);

        using (alice.AsOwner())
        {
            await alice.Os.Get<IConversation>(roomId).Say("Plan attached", attachments: [aliceLocalDoc]);
        }

        AttachmentRef aliceSharedDoc;
        using (bob.AsOwner())
        {
            var view = await bob.Os.Get<IConversation>(roomId).Read();
            var entry = view.Entries.OfType<Said>().Single(s => s.Text == "Plan attached");
            aliceSharedDoc = Assert.Single(entry.Attachments!);
            Assert.NotNull(aliceSharedDoc.BlobId);
            Assert.NotNull(aliceSharedDoc.BlobSha256);

            var (_, decrypted) = await bob.SharedAttachments.DownloadAndUnsealAsync(
                bob.Brain,
                aliceSharedDoc,
                expectedSenderKeys: bobFriendAlice.PublicKeys,
                persistToLocalStore: false,
                ct: Ct);
            Assert.Equal(docBytes, decrypted);
        }

        // 3. Stored relay blob bytes contain ZERO plaintext!
        var capturedBlobs = relay.CapturedBlobs.ToArray();
        Assert.True(capturedBlobs.Length >= 2);
        foreach (var blob in capturedBlobs)
        {
            var asLatin1 = Encoding.Latin1.GetString(blob.Ciphertext);
            Assert.DoesNotContain("RIFF", asLatin1, StringComparison.Ordinal);
            Assert.DoesNotContain("WAVE", asLatin1, StringComparison.Ordinal);
            Assert.DoesNotContain(docPlaintext, asLatin1, StringComparison.Ordinal);
        }

        // 4. Non-member OS-Eve gets uniform 404 from real HTTP GET /circle/v1/brains/blobs/{blobId}!
        using (var eveRawHttp = new HttpClient())
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{relay.Origin}/circle/v1/brains/blobs/{aliceSharedDoc.BlobId}");
            req.Headers.Add(LoopbackCloudRelayTestHost.BrainIdHeader, eve.Brain.Value);
            using var res = await eveRawHttp.SendAsync(req, Ct);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        Assert.Null(await eve.RelayClient.DownloadBlobAsync(aliceSharedDoc.BlobId!, ct: Ct));

        // 5. Even if OS-Eve obtains raw ciphertext bytes directly, BrainBlobSealer.UnsealAsync fails closed with CryptographicException!
        var rawDocCiphertext = capturedBlobs.Single(b => b.BlobId == aliceSharedDoc.BlobId).Ciphertext;
        var eveSigner = eve.Services.GetRequiredService<IBrainKeySigner>();
        await Assert.ThrowsAsync<CryptographicException>(async () =>
            await BrainBlobSealer.UnsealAsync(rawDocCiphertext, eve.Brain, eveSigner, bobFriendAlice.PublicKeys!, Ct));
    }

    [Fact]
    [Trait("Category", "TwoClusterMessengerE2E")]
    public async Task Test04_GroupRoomAgentSafeMentionPostureImmutabilityAndImmediateUnfriendRevocation()
    {
        await using var relay = await LoopbackCloudRelayTestHost.StartAsync(Ct);
        var recordingInference = new RecordingRoomInference();
        await using var alice = await IsolatedOsCluster.StartAsync("alice", "alice", relay, Ct, inference: recordingInference);
        await using var bob = await IsolatedOsCluster.StartAsync("bob", "bob", relay, Ct);

        // Pair Alice & Bob.
        Code code;
        using (alice.AsOwner())
        {
            code = await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Mint();
        }

        using (bob.AsOwner())
        {
            await bob.Os.Get<IFriends>(FriendsDefaults.Id("bob")).Redeem(code);
        }

        // 1. Alice creates a CrossBrainShared group room, invites Bob, and seats an agent with host-private settings -> MUST be confined!
        NeuronId roomId;
        using (alice.AsOwner())
        {
            var dir = alice.Os.Get<IThreadDirectory>(new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            roomId = await dir.CreateShared("Team Planning Room", ThreadKind.SharedRoom, "room-group-1");
            var conv = alice.Os.Get<IConversation>(roomId);
            await conv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Team Planning Room");
            await conv.Invite(new AccountId("bob"), bob.Brain, Role.Editor);

            // Attempt to seat an agent requesting private contexts + component operations -> SharedRoomAgentPolicy confines it.
            var requestedDef = new AgentDefinition
            {
                DisplayName = "@Ino · Room only",
                Instructions = "Help the team.",
                Contracts = [typeof(IConversation).FullName!, "IntoChat.OS.Modules.Files.IFiles"],
                Contexts = ["memory/private-secrets"],
                IncludeComponentOperations = true,
                OperationApps = ["files"],
            };
            var confinedDef = SharedRoomAgentPolicy.Confine(requestedDef, "Team Planning Room");
            Assert.Empty(confinedDef.Contexts);
            Assert.False(confinedDef.IncludeComponentOperations);
            Assert.Null(confinedDef.OperationApps);
            Assert.Null(confinedDef.ToolSources);
            Assert.All(confinedDef.Contracts, c => Assert.True(SharedRoomAgentPolicy.IsRoomSafeContract(c)));
            SharedRoomAgentPolicy.AssertConfined(confinedDef);

            await conv.Seat(SharedRoomAgentPolicy.DefaultRoomAgentId, requestedDef);

            var view = await conv.Read();
            Assert.Empty(view.Contexts);
            Assert.Contains(view.Participants, p => p == new Participant.Assistant(SharedRoomAgentPolicy.DefaultRoomAgentId));
        }

        // 2. Normal human messages from Alice and Bob trigger ZERO agent turns!
        using (alice.AsOwner())
        {
            await alice.Os.Get<IConversation>(roomId).Say("Hey Bob, ready for the sprint?");
        }

        using (bob.AsOwner())
        {
            await bob.Os.Get<IConversation>(roomId).Say("Yes! Let's review the backlog.");
        }

        await Task.Delay(150, Ct);
        Assert.Equal(0, recordingInference.CallCount);

        // 3. Explicit @room-ino mention triggers a confined agent turn with zero private-brain context exposure!
        using (bob.AsOwner())
        {
            await bob.Os.Get<IConversation>(roomId).Say($"@{SharedRoomAgentPolicy.DefaultRoomAgentId} summarize our sprint plan");
        }

        var mentionDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < mentionDeadline && recordingInference.CallCount < 1)
        {
            await Task.Delay(25, Ct);
        }

        Assert.Equal(1, recordingInference.CallCount);
        var lastRequest = recordingInference.LastRequest;
        Assert.NotNull(lastRequest);
        var systemText = string.Concat(lastRequest!.Messages
            .Where(static m => m.Role == "system")
            .SelectMany(static m => m.Content)
            .OfType<AiText>()
            .Select(static t => t.Text));
        Assert.Contains("zero access to any participant's private brain", systemText, StringComparison.OrdinalIgnoreCase);

        // 4. Posture immutability: OwnerTrusted <-> CrossBrainShared downgrade/upgrade rejected fail-closed on BOTH ConversationNeuron and ThreadDirectoryNeuron.CreateShared!
        using (alice.AsOwner())
        {
            var conv = alice.Os.Get<IConversation>(roomId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                conv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant));

            var dir = alice.Os.Get<IThreadDirectory>(new NeuronId(alice.Brain, Threads.Type, IThreadDirectory.DefaultKey));
            var privateEntry = await dir.Create("private-1", "My Private Thread", ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant);
            var privateThreadId = privateEntry.Thread!.Value;
            var privateConv = alice.Os.Get<IConversation>(privateThreadId);
            await privateConv.Configure(ThreadTrustPosture.OwnerTrusted, ThreadKind.DirectAssistant, "My Private Thread");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                privateConv.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dir.CreateShared("Attempt Upgrade", ThreadKind.SharedRoom, privateThreadId.Key));
        }

        // 5. Immediate Unfriend revocation: Alice unfriends Bob -> On(FriendRemoved) revokes offer & seat -> Bob Say/Read fail closed!
        using (alice.AsOwner())
        {
            await alice.Os.Get<IFriends>(FriendsDefaults.Id("alice")).Unfriend(new AccountId("bob"));
        }

        // Allow wire fan-out of FriendRemoved to settle on Alice's ConversationNeuron.
        var revokeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < revokeDeadline)
        {
            using (alice.AsOwner())
            {
                var humans = (await alice.Os.Get<IConversation>(roomId).Read()).Participants.OfType<Participant.Human>();
                if (humans.All(h => !string.Equals(h.Account.Value, "bob", StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(25, Ct);
        }

        using (bob.AsOwner())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IConversation>(roomId).Say("should be rejected after unfriend"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                bob.Os.Get<IConversation>(roomId).Read());
        }
    }

    private static DefaultHttpContext CreateSharedBlobDownloadContext(
        IsolatedOsCluster cluster,
        string account,
        BrainId recipientBrain,
        BrainId senderBrain,
        string blobSha256,
        string contentSha256,
        string fileName,
        string mediaType,
        string kind)
    {
        var reqServices = new ServiceCollection();
        reqServices.AddSingleton<ICallerAmbient>(new FixedCallerAmbient(cluster.Os.Edge.Person(account, account, recipientBrain)));
        reqServices.AddSingleton(cluster.Os.Edge);
        var http = new DefaultHttpContext { RequestServices = reqServices.BuildServiceProvider() };
        http.Request.QueryString = new QueryString(
            $"?sender={Uri.EscapeDataString(senderBrain.Value)}&sha256={blobSha256}&fileName={Uri.EscapeDataString(fileName)}&mediaType={Uri.EscapeDataString(mediaType)}&kind={Uri.EscapeDataString(kind)}&contentSha256={contentSha256}");
        return http;
    }

    private static byte[] CreateMinimalValidWavBytes()
    {
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

    private sealed class IsolatedOsCluster : IAsyncDisposable
    {
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _streamCts;
        private readonly Task _streamLoop;

        public required BrainId Brain { get; init; }
        public required string Account { get; init; }
        public required TestOS Os { get; init; }
        public required ServiceProvider Services { get; init; }
        public required KernelBrainKeyDirectory KeyDirectory { get; init; }
        public required BrainEnvelopeReceiver Receiver { get; init; }
        public required HttpBrainRelayClient RelayClient { get; init; }
        public required FileAttachmentStore Attachments { get; init; }
        public required SharedAttachmentRelay SharedAttachments { get; init; }
        public required string TempDir { get; init; }

        private IsolatedOsCluster(HttpClient http, CancellationTokenSource streamCts, Task streamLoop)
        {
            _http = http;
            _streamCts = streamCts;
            _streamLoop = streamLoop;
        }

        public IDisposable AsOwner() => Os.Edge.Enter(Os.Edge.Person(Account, Account, Brain));

        public static async Task<IsolatedOsCluster> StartAsync(
            string brainId,
            string account,
            LoopbackCloudRelayTestHost relay,
            CancellationToken ct,
            IModelInference? inference = null)
        {
            var brain = new BrainId(brainId);
            var tempDir = Path.Combine(Path.GetTempPath(), $"intochat-e2e-2c-{brainId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var relayClient = new HttpBrainRelayClient(
                http,
                relay.BaseUri,
                authorize: (request, _) =>
                {
                    request.Headers.Add(LoopbackCloudRelayTestHost.BrainIdHeader, brain.Value);
                    return ValueTask.CompletedTask;
                },
                timeProvider: TimeProvider.System,
                options: new BrainRelayClientOptions
                {
                    RecipientsHeaderName = BrainRelayTransportHosting.CircleBlobRecipientsHeader,
                    ContentSha256HeaderName = BrainRelayTransportHosting.CircleBlobContentSha256Header,
                    InitialReconnectDelay = TimeSpan.FromMilliseconds(50),
                });

            var relayProvider = new SingleBrainRelayProvider(relay.BaseUri, relayClient);

            TestOS? osRef = null;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(_ => osRef!.Edge);
            services.AddSingleton<ICallerAmbient>(new DelegatingCallerAmbient(() => osRef!.Callers));
            services.AddSingleton(_ => osRef!.Proxies);
            services.AddSingleton<IBrainSecretSealer, InMemoryBrainSecretSealer>();
            services.AddSingleton<IBrainKeySigner, IdentityBrainKeySigner>();
            services.AddSingleton<IBrainOwners>(new LocalOnlyBrainOwners(brainId, account));
            services.AddSingleton<ICodeBook, InMemoryCodeBook>();
            services.AddSingleton<ISecretRedactor, PassthroughRedactor>();
            services.AddSingleton<IBrainRelayClient>(relayClient);
            services.AddSingleton<IBrainRelayClientProvider>(relayProvider);
            services.AddSingleton(new FileAttachmentStore(tempDir, TimeProvider.System));
            services.AddSingleton<SharedAttachmentRelay>();
            services.AddSingleton<FriendsFriendCheck>();
            services.AddSingleton<IFriendCheck>(static sp => sp.GetRequiredService<FriendsFriendCheck>());
            services.AddSingleton<IFriendPeerDirectory>(static sp => sp.GetRequiredService<FriendsFriendCheck>());
            services.AddSingleton<KernelBrainKeyDirectory>(sp => new KernelBrainKeyDirectory(sp, sp.GetRequiredService<ICallerAmbient>()));
            services.AddSingleton<IBrainKeyDirectory>(static sp => sp.GetRequiredService<KernelBrainKeyDirectory>());
            if (inference is not null)
            {
                services.AddSingleton(inference);
            }

            SealedRelayBrainTransport? transport = null;
            services.AddSingleton<IBrainTransport>(new DelegatingBrainTransport(() => transport!));

            var provider = services.BuildServiceProvider();
            var signer = provider.GetRequiredService<IBrainKeySigner>();
            var keyDirectory = provider.GetRequiredService<KernelBrainKeyDirectory>();

            var os = osRef = TestOS.Local(options =>
            {
                options.Services = provider;
                options.Start = TimeProvider.System.GetUtcNow();
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
                timeProvider: TimeProvider.System,
                scopedVerifyCallerAccount: keyDirectory.VerifyCallerAccountForAsync,
                allowUnpinnedHandshake: BrainRelayTransportHosting.IsAllowedUnpinnedFriendHandshake);

            transport = new SealedRelayBrainTransport(
                signer: signer,
                keyLookup: (targetBrain, token) => keyDirectory.GetPinnedKeysForAsync(brain, targetBrain, token),
                callers: os.Callers,
                relay: relayClient.SendAndWaitReplyAsync,
                postRelay: relayClient.SendOneWayAsync,
                isRemote: b => !b.IsSystem && b != brain,
                localBrain: brain,
                timeProvider: TimeProvider.System,
                includeSenderPublicKeys: BrainRelayTransportHosting.IsFriendHandshakeCall);

            var streamCts = new CancellationTokenSource();
            var streamLoop = Task.Run(() => relayClient.RunStreamLoopAsync(receiver, streamCts.Token), CancellationToken.None);
            await relay.WaitForWebSocketConnectedAsync(brain.Value, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            return new IsolatedOsCluster(http, streamCts, streamLoop)
            {
                Brain = brain,
                Account = account,
                Os = os,
                Services = provider,
                KeyDirectory = keyDirectory,
                Receiver = receiver,
                RelayClient = relayClient,
                Attachments = provider.GetRequiredService<FileAttachmentStore>(),
                SharedAttachments = provider.GetRequiredService<SharedAttachmentRelay>(),
                TempDir = tempDir,
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _streamCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _streamLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Ignore clean cancellation / timeout on shutdown.
            }

            _streamCts.Dispose();
            _http.Dispose();
            await Os.DisposeAsync().ConfigureAwait(false);
            await Services.DisposeAsync().ConfigureAwait(false);
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, recursive: true);
            }
        }
    }

    private sealed class SingleBrainRelayProvider(Uri relayBaseUri, IBrainRelayClient client) : IBrainRelayClientProvider
    {
        public bool IsConfigured => true;
        public Uri RelayBaseUri => relayBaseUri;
        public IBrainRelayClient TryGetClient() => client;
    }

    private sealed class DelegatingCallerAmbient(Func<ICallerAmbient> resolve) : ICallerAmbient
    {
        public Caller? Current => resolve().Current;
    }

    private sealed class FixedCallerAmbient(Caller caller) : ICallerAmbient
    {
        public Caller? Current => caller;
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

    private sealed class DelegatingBrainTransport(Func<IBrainTransport> resolve) : IBrainTransport
    {
        public bool IsRemote(BrainId brain) => resolve().IsRemote(brain);
        public Task<JsonElement?> InvokeRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
            => resolve().InvokeRemoteAsync(target, call, cancellationToken);
        public Task PostRemoteAsync(NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken = default)
            => resolve().PostRemoteAsync(target, call, cancellationToken);
    }

    private sealed class RecordingRoomInference : IModelInference
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public InferenceRequest? LastRequest { get; private set; }

        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(new ModelDescriptor(
                "room-test",
                "room-test",
                null,
                "test",
                CapabilitySupport.Unsupported,
                CapabilitySupport.Unsupported,
                CapabilitySupport.Unsupported,
                8192,
                1024));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            Interlocked.Increment(ref _callCount);
            LastRequest = request;
            return Task.FromResult(new InferenceResult(
                [new AiMessage("assistant", [new AiText("Sprint plan summarized safely within room scope.")])],
                FinishReason: "stop",
                Usage: null,
                ResponseId: null,
                Model: "room-test"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            ModelId? model,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            string? brainId)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
