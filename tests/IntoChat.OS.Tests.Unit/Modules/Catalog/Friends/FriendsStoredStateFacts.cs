using System.Reflection;
using System.Text;
using DigitalBrain;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Social;
using IntoChat.OS.Modules.Identity.Contracts;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog.Friends;

// Frozen stored state of what people share with each other: the roster, the profile with its posts, the post store, a room,
// the index of rooms a person was invited to, and both sides of the file-share door. A neuron's state is written through
// RuntimeProtocol as JSON with no type name (KernelState.Write), so the JSON files pin what is in a brain. A record written
// by today's code has to read back after every later change to these records. UPDATE_GOLDEN=1 rewrites the files.
public sealed class FriendsStoredStateFacts
{
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    static FriendsStoredStateFacts()
    {
        // What AppsModule.ConfigureServices registers before a brain reads or writes these records.
        RuntimeProtocol.Register(CatalogContractsJson.Default);
        RuntimeProtocol.Register(FriendsJson.Default);
    }

    private static FriendsRecord Roster() => new()
    {
        Pending = [new PendingFriendCode("0A1B2C", At.AddHours(24))],
        Refused = 2,
        Friends =
        [
            new Friend(new AccountId("ben"), new BrainId("ben"), "Ben", At, new BrainPublicKeys([1, 2, 3], [4, 5, 6])),
            new Friend(new AccountId("eve"), new BrainId("eve-brain"), "Eve", At.AddDays(1)),
        ],
        Misses = 1,
        MissesSince = At.AddMinutes(5),
        Groups = [new GroupInvite(new BrainId("ben"), ConversationId.Parse("c-0123456789abcdef0123456789abcdef"), "Trip", new AccountId("ben"))],
        SigningKeyPkcs8 = [7, 8, 9],
        ExchangeKeyPkcs8 = [10, 11, 12],
    };

    private static ProfileState Profile() => new(
        "ada",
        "Builds things",
        ["calculator", "files"],
        [
            new ProfilePostItem(
                "post-2",
                "An app to fork",
                At.AddDays(2),
                "friends",
                "Notes",
                "customer-notes",
                "// template",
                [new PostEmbeddedApp("main", "customer-notes", "Customer Notes", "ada/app/customer-notes")],
                "post-1",
                new Dictionary<string, string> { ["App.cs"] = "// unit source" },
                "ada")
            { Audience = new Audience.Only([new AccountId("ben")]) },
            new ProfilePostItem("post-1", "Hello", At, "public") { Audience = new Audience.Friends() },
            new ProfilePostItem("post-0", "From before audiences", At.AddDays(-1), "family"),
        ],
        "Ada",
        "https://example.test/ada.png",
        [new ProfileFriend("ben", "ben", "Ben", "friends", At, "ben", true)],
        ["customer-notes"]);

    private static PostsState Posts() => new(
        [
            new SocialPost("p1", "ada", "Hello Ben", ["ben"], [new PostReaction("ben", "like", At.AddMinutes(1))], [new PostComment("c1", "ben", "Nice", At.AddMinutes(2))], 1, At),
            new SocialPost("p2", "ada", "My app", ["ben", "eve"], [], [], 2, At.AddHours(1), new AppDefinitionShare("customer-notes", "Customer Notes", [new SharedSourceFile("App.cs", "// unit source")])),
        ],
        2);

    private static RoomState Room() => new(
        3,
        [new RoomMessage(1, "ben", "hello", At), new RoomMessage(2, "ada", "hi", At.AddSeconds(5))],
        [new PinnedScreen("app.calculator", "room-calc")],
        [new RoomParticipant("ben", new BrainId("ben"), Role.Editor), new RoomParticipant("eve", new BrainId("eve-brain"), Role.Viewer)]);

    private static RoomsState Rooms() => new([new RoomEntry(new NeuronId(new BrainId("ada"), "social.room", "hangout"), 3, At)]);

    private static FileSharesRecord Door() => new()
    {
        Shares =
        [
            new WorkspaceShare("s1", "ben", "/work/trip", true, ShareRole.Editor, At),
            new WorkspaceShare("s2", "eve", "/work/notes.txt", false, ShareRole.Viewer, At.AddDays(1), Broken: true),
        ],
        Saves = [new FileShareSave("/work/trip/day1/b.txt", "ben", At.AddHours(2))],
    };

    private static SharedWithMeRecord SharedWithMe() => new()
    {
        Pointers = [new SharedPointer("ada", "ada", "s1", "/work/trip", true, ShareRole.Editor)],
    };

    private static readonly (string File, Func<object> Sample)[] Stored =
    [
        ("FriendsRecord.json", Roster),
        ("FriendsRecord.empty.json", static () => new FriendsRecord()),
        ("ProfileState.json", Profile),
        ("PostsState.json", Posts),
        ("RoomState.json", Room),
        ("RoomsState.json", Rooms),
        ("FileSharesRecord.json", Door),
        ("SharedWithMeRecord.json", SharedWithMe),
    ];

    public static TheoryData<string> Files() => [.. Stored.Select(static entry => entry.File)];

    [Theory, MemberData(nameof(Files))]
    public void TheRecordIsStoredAsFrozenJsonAndReadsBack(string file)
    {
        var sample = Stored.Single(entry => entry.File == file).Sample();
        Pin(file, RuntimeProtocol.SerializeToUtf8Bytes(sample, sample.GetType()));

        var read = RuntimeProtocol.Deserialize(File.ReadAllBytes(Golden(file)), sample.GetType());
        Assert.Equivalent(sample, read, strict: true);
    }

    // A later version may add a field. A brain that still runs this version must read such a record, not refuse it.
    [Theory, MemberData(nameof(Files))]
    public void ARecordWithAFieldThisVersionDoesNotKnowStillReads(string file)
    {
        var sample = Stored.Single(entry => entry.File == file).Sample();
        var stored = Encoding.UTF8.GetString(File.ReadAllBytes(Golden(file)));
        Assert.StartsWith("{", stored, StringComparison.Ordinal);
        var widened = "{\"addedLater\":{\"any\":[1,2]}," + stored[1..];

        var read = RuntimeProtocol.Deserialize(Encoding.UTF8.GetBytes(widened), sample.GetType());
        Assert.Equivalent(sample, read, strict: true);
    }

    // A roster written before friends, groups and keys existed: Friends stays null, which is what starts the one-time import.
    [Fact]
    public void ARosterFromBeforeFriendsWereStoredReadsAsNotImported()
    {
        var read = (FriendsRecord)RuntimeProtocol.Deserialize(
            """{"pending":[{"hash":"0A1B","expiresAt":"2026-11-21T00:00:00+00:00"}],"refused":3}"""u8, typeof(FriendsRecord))!;

        Assert.Equal(("0A1B", 3), (read.Pending.Single().Hash, read.Refused));
        Assert.Null(read.Friends);
        Assert.Empty(read.Groups);
        Assert.Null(read.SigningKeyPkcs8);
        Assert.Null(read.ExchangeKeyPkcs8);
    }

    [Fact]
    public void NoStoredFormNamesAClrTypeOrAssembly()
    {
        foreach (var (file, _) in Stored)
        {
            Assert.DoesNotContain("IntoChat", Encoding.UTF8.GetString(File.ReadAllBytes(Golden(file))), StringComparison.Ordinal);
        }
    }

    // What addresses each record in a brain: the neuron type id, the state name and the key. None is derived from a CLR name.
    [Fact]
    public void TheNeuronsKeepTheirTypeIdsStateNamesAndKeys()
    {
        Assert.Equal(
            [
                "files.shared-with-me|state",
                "files.shares|state",
                "friends.links|state",
                "os.profile|state",
                "social.posts|state",
                "social.rooms|state",
                "social.room|state",
            ],
            new[] { typeof(FriendsNeuron), typeof(ProfileNeuron), typeof(PostsNeuron), typeof(RoomNeuron), typeof(RoomsNeuron), typeof(FileSharesNeuron), typeof(SharedWithMeNeuron) }
                .Select(static type => type.GetCustomAttribute<NeuronAttribute>(false)!)
                .Select(static neuron => $"{neuron.Type}|{neuron.State ?? "state"}")
                .Order(StringComparer.Ordinal));

        Assert.Equal("owner/friends.links/friends", FriendsDefaults.Id("owner").ToString());
        Assert.Equal("owner/files.shares/files/shares", FileSharesDefaults.Id("owner").ToString());
        Assert.Equal("owner/files.shared-with-me/files/shared-with-me", SharedWithMeDefaults.Id("owner").ToString());
        Assert.Equal("default", IPosts.DefaultKey);
        Assert.Equal("default", IRooms.DefaultKey);
    }

    // A contract is stored by its full name (agent definitions, grants, slot specs) and called by its alias.
    [Fact]
    public void TheContractsKeepTheirFullNamesAliasesAndDefaultTypes()
    {
        Assert.Equal(
            [
                "IntoChat.OS.Modules.Apps.IFileShares|files.shares|files.shares",
                "IntoChat.OS.Modules.Apps.IFriends|friends|friends.links",
                "IntoChat.OS.Modules.Apps.IProfile|os.profile|os.profile",
                "IntoChat.OS.Modules.Apps.ISharedWithMe|files.shared-with-me|files.shared-with-me",
                "IntoChat.OS.Modules.Apps.Social.IPosts|social.posts|social.posts",
                "IntoChat.OS.Modules.Apps.Social.IRooms|social.rooms|social.rooms",
                "IntoChat.OS.Modules.Apps.Social.IRoom|social.room|social.room",
            ],
            new[] { typeof(IFriends), typeof(IProfile), typeof(IPosts), typeof(IRoom), typeof(IRooms), typeof(IFileShares), typeof(ISharedWithMe) }
                .Select(static contract => $"{contract.FullName}|{contract.GetCustomAttribute<NeuronAliasAttribute>(false)!.Alias}|{PlainNeuronContracts.TypeOf(contract)}")
                .Order(StringComparer.Ordinal));
    }

    // A signal is journaled under its alias.
    [Fact]
    public void TheSignalsKeepTheirAliases()
    {
        Assert.Equal("os.profile.changed", typeof(ProfileChanged).GetCustomAttribute<SignalAttribute>(false)!.Alias);
        Assert.Equal("files.shares.changed", typeof(FileSharesChanged).GetCustomAttribute<SignalAttribute>(false)!.Alias);
        Assert.Equal("files.share.served", typeof(FileShareServed).GetCustomAttribute<SignalAttribute>(false)!.Alias);
    }

    private static string Golden(string name) => Path.Combine(AppContext.BaseDirectory, "FriendsGolden", name);

    private static void Pin(string name, byte[] actual)
    {
        Assert.NotEmpty(actual);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(Path.GetDirectoryName(IntoChat.OS.Aspire.Hosting.Tests.Unit.DeploymentSnapshot.ProjectPath("IntoChat.OS.Tests.Unit"))!, "Modules", "Catalog", "Friends", "Golden", name);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllBytes(source, actual);
            Directory.CreateDirectory(Path.GetDirectoryName(Golden(name))!);
            File.WriteAllBytes(Golden(name), actual);
            return;
        }
        Assert.True(File.Exists(Golden(name)), $"Golden file {name} is missing; run with UPDATE_GOLDEN=1 once.");
        Assert.Equal(Encoding.UTF8.GetString(File.ReadAllBytes(Golden(name))), Encoding.UTF8.GetString(actual));
    }
}
