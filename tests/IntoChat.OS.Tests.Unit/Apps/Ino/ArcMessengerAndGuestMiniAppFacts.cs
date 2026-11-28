using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Sealing;
using DigitalBrain.Components;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.OS.Tests.Support.Fakes;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Apps.Social;
using IntoChat.OS.Host.Kernel;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Conversations;
using IntoChat.OS.Modules.Identity.Contracts;
using IntoChat.OS.Modules.Notifications;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Apps.Tests.Unit;
using IntoChat.OS.Tests;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Apps.Ino;

public sealed class ArcMessengerAndGuestMiniAppFacts
{
    private static readonly BrainId AliceBrain = new("alice");
    private static readonly BrainId BobBrain = new("bob");
    private static readonly BrainId EveBrain = new("eve");

    [Fact]
    public void ArcSidebarRenders264pxSplitPaneWithAllSectionsAndTrustHeaderBadges()
    {
        var entries = new ThreadEntry[]
        {
            new("fav-ino", "@Ino", DateTimeOffset.UtcNow, false, IsFavourite: true, Kind: ThreadKind.DirectAssistant),
            new("pin-1", "Pinned Brief", DateTimeOffset.UtcNow, false, IsPinned: true, Kind: ThreadKind.DirectAssistant, UnreadCount: 2),
            new("squad-1", "Release Squad", DateTimeOffset.UtcNow, false, Folder: "Projects", Kind: ThreadKind.Squad),
            new("room-1", "Design Sync", DateTimeOffset.UtcNow, false, Kind: ThreadKind.SharedRoom, TrustPosture: ThreadTrustPosture.CrossBrainShared, UnreadCount: 3),
            new("today-1", "Quick Question", DateTimeOffset.UtcNow, false, Kind: ThreadKind.DirectAssistant),
        };
        var shelf = new ThreadShelf(
            Favourites: [entries[0]],
            Pinned: [entries[1]],
            Folders: ["Projects"],
            Entries: entries,
            TotalUnread: 5);
        var archived = new ThreadPage([new("old-1", "Archived Thread", DateTimeOffset.UtcNow.AddDays(-2), true)], 1);

        var sharedView = new ConversationView(
            ConversationId.Parse("room-1"),
            4,
            [],
            [],
            null,
            [],
            false,
            [])
        {
            TrustPosture = ThreadTrustPosture.CrossBrainShared,
            Kind = ThreadKind.SharedRoom,
            PinnedScreens = [new PinnedScreen("room.checklist", "default")],
        };

        var state = new InoAppState
        {
            ConversationId = "room-1",
            Title = "Design Sync",
            NewMenuOpen = true,
            HistoryOpen = true,
            DrawerOpen = true,
            DrawerTab = InoArcSidebar.DrawerTabPinnedApps,
        };

        var root = InoView.ScreenNode(
            state,
            VoiceAbilities.None,
            _ => new Text("thread"),
            view: sharedView,
            shelf: shelf,
            archivedSessions: archived,
            hostBrainId: "alice");

        var split = Assert.IsType<SplitPane>(root);
        Assert.Equal(264, split.LeadingExtent);

        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        Assert.Contains("Favourites", json, StringComparison.Ordinal);
        Assert.Contains("Pinned", json, StringComparison.Ordinal);
        Assert.Contains("Squads & Projects", json, StringComparison.Ordinal);
        Assert.Contains("People & Rooms", json, StringComparison.Ordinal);
        Assert.Contains("Today", json, StringComparison.Ordinal);
        Assert.Contains("History (1)", json, StringComparison.Ordinal);
        Assert.Contains("New chat", json, StringComparison.Ordinal);
        Assert.Contains("New squad", json, StringComparison.Ordinal);
        Assert.Contains("New room / DM", json, StringComparison.Ordinal);
        Assert.Contains("New specialist", json, StringComparison.Ordinal);
        Assert.Contains("CrossBrainShared · E2EE", json, StringComparison.Ordinal);
        Assert.Contains("@Ino · Room only", json, StringComparison.Ordinal);
        Assert.Contains("Pinned Apps", json, StringComparison.Ordinal);
        Assert.Contains("alice/apps/room.checklist/view", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestAppsNodeAndActCapabilityMatrixEnforcesFailClosedSecurity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var os = TestOS.Local()
            .RegisterCatalog()
            .Register<RoomChecklistApp>()
            .Register(typeof(UiModule).Assembly);

        var hostedNeurons = new StubHostedNeuronTypes(new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["room.checklist"] = typeof(RoomChecklistApp),
            ["app.settings"] = typeof(SettingsApp),
            ["app.ino"] = typeof(InoApp),
            ["non.shareable"] = typeof(NonShareableDummyScreen),
        });

        var proxies = new TestOsNeuronProxies(os);
        var checklistId = new NeuronId(AliceBrain, "room.checklist", "default");
        var offers = os.Get<IOffers>(new NeuronId(AliceBrain, "component.offers", IOffers.DefaultKey));

        // Seed initial state on Alice's checklist as Alice
        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("alice", "alice", AliceBrain)))
        {
            await os.Get<IScreen>(checklistId).Dispatch(new Command(".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Host item" })));
            await offers.Offer(checklistId, "bob", Role.Editor, ct);
            await offers.Offer(checklistId, "carol", Role.Viewer, ct);
        }

        // 1. Seated Editor (Bob) CAN read /apps/node and act via /act, preserving Caller.For.Account == "bob"
        var bobCaller = TestEdge.Authority.Person("bob", "bob", BobBrain);
        using (TestEdge.Authority.Enter(bobCaller))
        {
            var readResult = await AppUiEndpoints.ReadNode(
                UIVocabulary.ViewType,
                "alice/apps/room.checklist/view",
                proxies,
                BobBrain.Value,
                ct,
                caller: bobCaller,
                hostedNeurons: hostedNeurons,
                edge: TestEdge.Authority);
            Assert.NotNull(readResult);

            var actResult = await AppUiEndpoints.Act(
                new AppUiEndpoints.ActRequest(
                    "alice/apps/room.checklist",
                    ".",
                    "room-checklist.add",
                    JsonSerializer.SerializeToElement(new { label = "Bob item" })),
                proxies,
                BobBrain.Value,
                ct,
                bobCaller,
                hostedNeurons: hostedNeurons,
                edge: TestEdge.Authority);
            Assert.NotNull(actResult);
        }

        // Verify caller attribution was preserved (Bob, NEVER elevated to Alice!)
        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("alice", "alice", AliceBrain)))
        {
            var view = await os.Get<IScreen>(checklistId).Read();
            var viewJson = JsonSerializer.Serialize(view);
            Assert.Contains("Bob item", viewJson, StringComparison.Ordinal);
            Assert.Contains("by bob", viewJson, StringComparison.Ordinal);
        }

        // 2. Seated Viewer (Carol) CAN read /apps/node, but is DENIED on /act
        var carolCaller = TestEdge.Authority.Person("carol", "carol", new BrainId("carol"));
        using (TestEdge.Authority.Enter(carolCaller))
        {
            var carolRead = await AppUiEndpoints.ReadNode(
                UIVocabulary.ViewType,
                "alice/apps/room.checklist/view",
                proxies,
                "carol",
                ct,
                caller: carolCaller,
                hostedNeurons: hostedNeurons,
                edge: TestEdge.Authority);
            Assert.NotNull(carolRead);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.Act(
                    new AppUiEndpoints.ActRequest("alice/apps/room.checklist", ".", "room-checklist.add", JsonSerializer.SerializeToElement(new { label = "Viewer hack" })),
                    proxies,
                    "carol",
                    ct,
                    carolCaller,
                    hostedNeurons: hostedNeurons,
                    edge: TestEdge.Authority));
        }

        // 3. Unseated caller (Eve) is DENIED on both /apps/node and /act
        var eveCaller = TestEdge.Authority.Person("eve", "eve", EveBrain);
        using (TestEdge.Authority.Enter(eveCaller))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "alice/apps/room.checklist/view", proxies, EveBrain.Value, ct, caller: eveCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.Act(new AppUiEndpoints.ActRequest("alice/apps/room.checklist", ".", "room-checklist.add"), proxies, EveBrain.Value, ct, eveCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
        }

        // 4. Host-private apps & non-[Shareable] apps are ALWAYS DENIED fail-closed
        using (TestEdge.Authority.Enter(bobCaller))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "alice/apps/app.settings/view", proxies, BobBrain.Value, ct, caller: bobCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "alice/apps/app.ino/view", proxies, BobBrain.Value, ct, caller: bobCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "alice/apps/non.shareable/view", proxies, BobBrain.Value, ct, caller: bobCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
        }

        // 5. Revoked/unfriended guest (Bob) is IMMEDIATELY DENIED
        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("alice", "alice", AliceBrain)))
        {
            await offers.Revoke(checklistId, "bob", ct);
        }

        using (TestEdge.Authority.Enter(bobCaller))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.ReadNode(UIVocabulary.ViewType, "alice/apps/room.checklist/view", proxies, BobBrain.Value, ct, caller: bobCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                AppUiEndpoints.Act(new AppUiEndpoints.ActRequest("alice/apps/room.checklist", ".", "room-checklist.add"), proxies, BobBrain.Value, ct, bobCaller, hostedNeurons: hostedNeurons, edge: TestEdge.Authority));
        }
    }

    [Fact]
    public async Task RoomInoExecutesRoomSafeActToolOnPinnedChecklistAndBlocksPrivateTools()
    {
        var toolDriver = new ScriptedRoomToolInference();
        var sp = new ServiceCollection()
            .AddSingleton<IModelInference>(toolDriver)
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp)
            .RegisterCatalog()
            .Register<RoomChecklistApp>()
            .Register(typeof(UiModule).Assembly)
            .Register(typeof(IntoChat.OS.Modules.AI.AIModule).Assembly);

        var roomId = new NeuronId(AliceBrain, IConversation.Type, "conversations/room-tools");
        var checklistId = new NeuronId(AliceBrain, "room.checklist", "default");

        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("alice", "alice", AliceBrain)))
        {
            var room = os.Get<IConversation>(roomId);
            await room.Configure(ThreadTrustPosture.CrossBrainShared, ThreadKind.SharedRoom, "Trip Planning");
            await room.Pin("room.checklist", "default");

            await room.Say("@Ino please add Pack passports to our checklist");
            await os.Idle();

            var view = await os.Get<IScreen>(checklistId).Read();
            var viewJson = JsonSerializer.Serialize(view);
            Assert.Contains("Pack passports", viewJson, StringComparison.Ordinal);

            // Verify blocked tool call returned error and never leaked private data
            Assert.Contains(toolDriver.ObservedToolResults, r => r.Contains("not allowed in a CrossBrainShared room", StringComparison.Ordinal));
            Assert.Contains(toolDriver.ObservedToolResults, r => r.Contains("not pinned in this room", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task NotificationsInboxAndUnreadCountIncrementOnInviteAndChangeThenClearOnMarkRead()
    {
        var sp = new ServiceCollection()
            .AddSingleton(TestEdge.Authority)
            .AddSingleton<IntoChat.OS.Modules.Apps.IBrainOwners>(new StaticBrainOwners())
            .BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp)
            .RegisterCatalog()
            .Register(typeof(ThreadDirectoryNeuron).Assembly)
            .Register(typeof(NotificationsModule).Assembly);

        var roomId = new NeuronId(BobBrain, IConversation.Type, "conversations/dm-bob");

        using (TestEdge.Authority.Enter(TestEdge.Authority.Person("bob", "bob", BobBrain)))
        {
            var bobDirectory = os.Get<IThreadDirectory>(new NeuronId(BobBrain, Threads.Type, IThreadDirectory.DefaultKey));
            var bobInbox = os.Get<INotificationsInbox>(INotificationsInbox.Key(BobBrain.Value));

            await bobDirectory.Invited(roomId, "Alice DM", ThreadKind.DirectPerson);
            await bobDirectory.Changed(roomId, 3);

            var shelf = await bobDirectory.Shelf();
            Assert.True(shelf.TotalUnread >= 3);
            var notifications = await bobInbox.ListAsync(limit: 10, unreadOnly: true, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(notifications.Length >= 2);
            Assert.Contains(notifications, n => n.Category == "messenger.invite");
            Assert.Contains(notifications, n => n.Category == "messenger.dm");

            var entryId = Threads.EntryIdFor(BobBrain, roomId);
            await bobDirectory.MarkRead(entryId, 3);
            var afterRead = await bobDirectory.Shelf();
            Assert.Equal(0, afterRead.TotalUnread);
        }
    }

    [Fact]
    public async Task SocialFeedSyncRunsFriendsInParallelIsolatesErrorsAndPropagatesCancellation()
    {
        var ct = TestContext.Current.CancellationToken;
        var inFlight = 0;
        var maxConcurrent = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var roster = new Friend[]
        {
            new(new AccountId("bob"), BobBrain, "Bob", DateTimeOffset.UtcNow),
            new(new AccountId("carol"), new BrainId("carol"), "Carol", DateTimeOffset.UtcNow),
            new(new AccountId("broken"), new BrainId("broken"), "Broken", DateTimeOffset.UtcNow),
        };

        var result = await SocialFeedSync.SyncAsync(
            AliceBrain,
            ownPosts: new StubPosts([MakePost("p-own", "alice", 2)], 2),
            roster: roster,
            resolveFriendPosts: brain =>
            {
                if (brain.Value == "broken")
                {
                    return new ThrowingPosts();
                }

                return new BarrierPosts(brain.Value, () =>
                {
                    var current = Interlocked.Increment(ref inFlight);
                    int prev;
                    do { prev = maxConcurrent; }
                    while (current > prev && Interlocked.CompareExchange(ref maxConcurrent, current, prev) != prev);
                    if (current >= 2) { gate.TrySetResult(); }
                    return gate.Task;
                });
            },
            cursors: new Dictionary<string, long>(StringComparer.Ordinal) { ["bob"] = 1 },
            ct: ct);

        Assert.True(maxConcurrent >= 2, $"Expected parallel Task.WhenAll fan-out, observed maxConcurrent={maxConcurrent}.");
        Assert.Contains("broken", result.FailedBrains);
        Assert.Equal(3, result.Posts.Length);
        Assert.Equal(2, result.NextCursors[AliceBrain.Value]);
        Assert.Equal(5, result.NextCursors["bob"]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            SocialFeedSync.SyncAsync(AliceBrain, null, roster, _ => new StubPosts([], 0), ct: cts.Token));
    }

    [Fact]
    public async Task SettingsPeopleAndTeamHandlesFriendCodesRosterPermissionsAndUnfriend()
    {
        var sp = new ServiceCollection()
            .AddSingleton(TestEdge.Authority)
            .AddSingleton<IBrainSecretSealer, InMemoryBrainSecretSealer>()
            .AddSingleton<IntoChat.OS.Modules.Identity.Contracts.Codes.ICodeBook, TestCodeBook>()
            .AddSingleton<IntoChat.OS.Modules.Apps.IBrainOwners>(new StaticBrainOwners())
            .BuildServiceProvider();
        using var person = TestEdge.Authority.Enter(TestEdge.Authority.Person("local", "local", new BrainId("local")));
        await using var os = TestOS.Local(o => o.Services = sp)
            .RegisterCatalog()
            .Register(typeof(IntoChat.OS.Modules.Identity.Grants.GrantRules).Assembly);
        os.SeedStoredState(
            FriendsDefaults.Id("local"),
            """{"pending":[],"refused":0,"friends":[{"account":"grace","brain":"grace-brain","name":"Grace Hopper","since":"2026-01-01T00:00:00+00:00"}]}""");

        var settings = await os.Install<SettingsApp>();
        await settings.Request("navigate", new SettingsNavigate("team"));

        // Mint 24h friend code
        await settings.Request("mint-friend-code", new SettingsMintFriendCode());
        Assert.True(string.IsNullOrEmpty(settings.App.State.TeamError), $"Unexpected TeamError: {settings.App.State.TeamError}");
        Assert.False(string.IsNullOrWhiteSpace(settings.App.State.ActiveFriendCode));
        Assert.NotNull(settings.App.State.ActiveFriendCodeExpiresAt);

        // @handle lookup falls back cleanly to roster
        await settings.Request("lookup-handle", new SettingsLookupHandle("@grace"));
        Assert.Contains("@grace", settings.App.State.HandleLookupResult, StringComparison.Ordinal);

        // Grant and revoke connection permission
        await settings.Request("set-friend-permission", new SettingsSetFriendPermission("grace", "rooms.edit", Allowed: true));
        Assert.Contains("grace:rooms.edit", settings.App.State.FriendConnectionGrants);
        await settings.Request("set-friend-permission", new SettingsSetFriendPermission("grace", "rooms.edit", Allowed: false));
        Assert.DoesNotContain("grace:rooms.edit", settings.App.State.FriendConnectionGrants);

        // Unfriend removes from roster
        await settings.Request("unfriend", new SettingsUnfriend("grace"));
        Assert.DoesNotContain(settings.App.State.TeamRoster ?? [], f => f.Account.Value == "grace");
    }

    private static SocialPost MakePost(string id, string author, long seq)
        => new(id, author, $"Post {id}", [], [], [], seq, DateTimeOffset.UtcNow);

    private sealed record DummyScreenState;

    private sealed class NonShareableDummyScreen : Screen<DummyScreenState>
    {
        protected override View Render(DummyScreenState state) => new("Dummy", new Text("private"));
    }

    private sealed class TestOsNeuronProxies(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
        public T Get<T>(string key) where T : class => os.Get<T>(NeuronId.Parse(key));
    }

#pragma warning disable IDE0060
    private sealed class StubHostedNeuronTypes(IReadOnlyDictionary<string, Type> map) : IHostedNeuronTypes
    {
        public bool IsHosted(string neuronType) => map.ContainsKey(neuronType);
        public bool IsContractHosted(string contractFullName) => true;
        public bool TryGetNeuronClass(string neuronType, out Type? neuronClass) => map.TryGetValue(neuronType, out neuronClass);
    }

    private sealed class ScriptedRoomToolInference : IModelInference
    {
        private int _step;
        public List<string> ObservedToolResults { get; } = [];

        public bool HasProfile(string name) => false;

        public Task<ModelDescriptor> Describe(ModelSelection? selection = null, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
            => Task.FromResult(new ModelDescriptor("test", "test-model", null, "1", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
        {
            foreach (var msg in request.Messages.Where(static m => m.Role == "tool"))
            {
                foreach (var res in msg.Content.OfType<AiToolResult>())
                {
                    ObservedToolResults.Add(res.ResultJson);
                }
            }

            if (_step++ == 0)
            {
                return Task.FromResult(new InferenceResult(
                    [
                        new AiMessage("assistant",
                        [
                            new AiToolCall("c1", SharedRoomAgentPolicy.RoomActToolName, """{"screenType":"room.checklist","screenKey":"default","signal":"room-checklist.add","args":{"label":"Pack passports"}}"""),
                            new AiToolCall("c2", "memory_recall", """{"query":"secrets"}"""),
                            new AiToolCall("c3", SharedRoomAgentPolicy.RoomActToolName, """{"screenType":"app.files","screenKey":"default","signal":"files.read"}"""),
                        ])
                    ],
                    "tool_calls",
                    null,
                    "r1",
                    "test-model"));
            }

            return Task.FromResult(new InferenceResult(
                [new AiMessage("assistant", [new AiText("Added 'Pack passports' to the shared checklist!")])],
                "stop",
                null,
                "r2",
                "test-model"));
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default, string? brainId = null)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubPosts(SocialPost[] posts, long nextCursor) : IPosts
    {
        public Task<string> Publish(PostDraft draft, CancellationToken ct = default) => Task.FromResult("id");
        public Task<PostPage> Since(long cursor = 0, CancellationToken ct = default) => Task.FromResult(new PostPage(posts, nextCursor));
        public Task React(string postId, string reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> Comment(string postId, string text, CancellationToken ct = default) => Task.FromResult("c1");
        public Task Delete(string postId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BarrierPosts(string brain, Func<Task> onEnter) : IPosts
    {
        public Task<string> Publish(PostDraft draft, CancellationToken ct = default) => Task.FromResult("id");
        public async Task<PostPage> Since(long cursor = 0, CancellationToken ct = default)
        {
            await onEnter().WaitAsync(TimeSpan.FromSeconds(5), ct);
            return new PostPage([MakePost($"p-{brain}", brain, 5)], 5);
        }
        public Task React(string postId, string reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> Comment(string postId, string text, CancellationToken ct = default) => Task.FromResult("c1");
        public Task Delete(string postId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingPosts : IPosts
    {
        public Task<string> Publish(PostDraft draft, CancellationToken ct = default) => throw new InvalidOperationException("offline");
        public Task<PostPage> Since(long cursor = 0, CancellationToken ct = default) => throw new InvalidOperationException("offline");
        public Task React(string postId, string reaction, CancellationToken ct = default) => throw new InvalidOperationException("offline");
        public Task<string> Comment(string postId, string text, CancellationToken ct = default) => throw new InvalidOperationException("offline");
        public Task Delete(string postId, CancellationToken ct = default) => throw new InvalidOperationException("offline");
    }

    private sealed class TestCodeBook : IntoChat.OS.Modules.Identity.Contracts.Codes.ICodeBook
    {
        private readonly Dictionary<string, (IntoChat.OS.Modules.Identity.Contracts.Codes.CodePurpose Purpose, IntoChat.OS.Modules.Identity.Contracts.Codes.CodeTarget Target)> _targets = new(StringComparer.Ordinal);
        public Task<string> Bind(Code code, IntoChat.OS.Modules.Identity.Contracts.Codes.CodePurpose purpose, BrainId brain, string reference, DateTimeOffset expiresAt)
        {
            var hash = "h:" + code.Value;
            _targets[hash] = (purpose, new IntoChat.OS.Modules.Identity.Contracts.Codes.CodeTarget(brain, reference, expiresAt));
            return Task.FromResult(hash);
        }
        public Task<string> HashOf(Code code) => Task.FromResult("h:" + code.Value);
        public Task<IntoChat.OS.Modules.Identity.Contracts.Codes.CodeTarget?> Find(IntoChat.OS.Modules.Identity.Contracts.Codes.CodePurpose purpose, string hash)
            => Task.FromResult(_targets.TryGetValue(hash, out var entry) && entry.Purpose == purpose ? entry.Target : null);
        public Task<IntoChat.OS.Modules.Identity.Contracts.Codes.CodeTarget?> Lookup(IntoChat.OS.Modules.Identity.Contracts.Codes.CodePurpose purpose, string hash)
            => Find(purpose, hash);
        public Task Spend(string hash) { _targets.Remove(hash); return Task.CompletedTask; }
    }

    private sealed class StaticBrainOwners : IntoChat.OS.Modules.Apps.IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken ct = default) => Task.FromResult<string?>(brain.Value);
    }
#pragma warning restore IDE0060
}
