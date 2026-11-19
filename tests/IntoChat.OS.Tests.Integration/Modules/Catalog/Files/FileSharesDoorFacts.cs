using DigitalBrain.Access;
using IntoChat.OS.Tests;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Flutter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Files;

// The door of a brain's shared files is the one thing a person of another brain can reach. These facts prove it is
// exactly that wide: each denial case differs from the served case in one respect only.
public sealed class FileSharesDoorFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal const string Ada = "ada";
    internal const string Ben = "ben";
    internal const string Eve = "eve";

    [Fact]
    public async Task A_viewer_reads_and_lists_through_the_relay()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using var ben = As(Ben);
        var bytes = await world.Mine(Ben).Read(Ada, id, "day1/a.txt");
        var listed = await world.Mine(Ben).List(Ada, id, "");

        Assert.Equal("hello", Encoding.UTF8.GetString(bytes!));
        Assert.Contains(listed, entry => entry is { Path: "day1", IsFolder: true });
    }

    [Fact]
    public async Task The_door_refuses_a_call_that_did_not_come_through_the_relay()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using (As(Ben)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Read(id, "day1/a.txt")); }
        using (As(Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Read(id, "day1/a.txt")); }
        // Ben's account acting in a brain it does not own.
        using (As(Ben, brain: Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Read(id, "day1/a.txt")); }
    }

    [Theory]
    [InlineData(Who.App)]
    [InlineData(Who.Agent)]
    public async Task An_app_or_an_agent_is_refused_behind_the_relay_and_at_the_door(Who kind)
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);

        using var notAPerson = As(Ben, kind: kind);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Read(id, "day1/a.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Write(id, "day1/b.txt", [1]));
    }

    [Fact]
    public async Task The_door_refuses_another_neuron_of_the_friends_brain()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using var ben = As(Ben);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Prober(Ben).ReadDoor(Ada, id, "day1/a.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Prober(Ben).ReadDoorThroughInner(Ada, id, "day1/a.txt"));
    }

    [Fact]
    public async Task A_neuron_that_is_not_a_listed_screen_cannot_drive_the_relay()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using var ben = As(Ben);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Prober(Ben).ReadThroughRelay(Ada, id, "day1/a.txt"));
    }

    [Fact]
    public async Task A_person_the_share_does_not_name_is_refused_through_their_own_relay()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using (As(Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).Read(Ada, id, "day1/a.txt")); }
        using (As(Ben, brain: Eve)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Eve).Read(Ada, id, "day1/a.txt")); }
    }

    [Fact]
    public async Task A_removed_share_is_refused_on_the_next_call_and_its_pointer_goes()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);
        using (As(Ben))
        {
            await world.Mine(Ben).Add(Ada, id);
            Assert.Single(await world.Mine(Ben).Pointers());
            Assert.NotNull(await world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
        }

        using (As(Ada)) { await world.Door(Ada).Unshare(id); }

        using (As(Ben))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, id, "day1/a.txt"));
            Assert.Empty(await world.Mine(Ben).Pointers());
        }
    }

    [Theory]
    [InlineData("../private/secret.txt")]
    [InlineData("/work/private/secret.txt")]
    [InlineData("day1/../../private/secret.txt")]
    [InlineData("/local/secret.txt")]
    public async Task The_door_refuses_a_path_outside_the_share(string path)
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Editor);

        using (As(Ben))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => world.Mine(Ben).Read(Ada, id, path));
            await Assert.ThrowsAsync<ArgumentException>(() => world.Mine(Ben).Write(Ada, id, path, [1]));
        }

        Assert.Empty(await world.Served());
    }

    [Fact]
    public async Task A_file_of_the_same_name_elsewhere_is_never_served_for_a_missing_one()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        // "/work/private/secret.txt" exists; "/work/trip/secret.txt" does not.
        using var ben = As(Ben);
        Assert.Null(await world.Mine(Ben).Read(Ada, id, "secret.txt"));
    }

    [Fact]
    public async Task A_viewer_cannot_write_and_an_editor_can()
    {
        await using var world = await World.Start();
        var viewer = await world.ShareTrip(ShareRole.Viewer);
        var bytes = Encoding.UTF8.GetBytes("from ben");

        using (As(Ben)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Write(Ada, viewer, "day1/b.txt", bytes)); }

        var editor = await world.ShareTrip(ShareRole.Editor);
        using (As(Ben))
        {
            var written = await world.Mine(Ben).Write(Ada, editor, "day1/b.txt", bytes);
            Assert.Equal(Ben, written.ModifiedBy);
            // The earlier share of the same folder was replaced, not kept beside the new one.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mine(Ben).Read(Ada, viewer, "day1/a.txt"));
        }

        using (As(Ada)) { Assert.Equal("from ben", await world.ReadAsOwner("/work/trip/day1/b.txt")); }
    }

    [Fact]
    public async Task Only_the_owner_acting_as_a_person_shares()
    {
        await using var world = await World.Start();

        using (As(Ada, kind: Who.App)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Share(Ben, "/work/trip", ShareRole.Viewer)); }
        using (As(Ada, kind: Who.Agent)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Share(Ben, "/work/trip", ShareRole.Viewer)); }
        using (As(Ben, brain: Ada)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Share(Ben, "/work/trip", ShareRole.Viewer)); }
        using (As(Ben, brain: Ada)) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Door(Ada).Shares()); }
        using (As(Ada)) { Assert.Empty(await world.Door(Ada).Shares()); }
    }

    [Theory]
    [InlineData("/local/trip")]
    [InlineData("/work")]
    [InlineData("/work/nothing-here")]
    public async Task Only_an_existing_path_of_the_workspace_can_be_shared(string path)
    {
        await using var world = await World.Start();

        using var ada = As(Ada);
        await Assert.ThrowsAsync<ArgumentException>(() => world.Door(Ada).Share(Ben, path, ShareRole.Viewer));
    }

    [Fact]
    public async Task Every_served_call_is_recorded_in_the_owners_brain()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        using (As(Ben)) { await world.Mine(Ben).Read(Ada, id, "day1/a.txt"); }

        var served = Assert.Single(await world.Served());
        Assert.Equal(new FileShareServed(id, Ben, "/work/trip/day1/a.txt", Write: false), served);
    }

    [Fact]
    public async Task A_share_whose_folder_was_renamed_is_marked_broken_for_the_owner()
    {
        await using var world = await World.Start();
        await world.ShareTrip(ShareRole.Viewer);
        using var ada = As(Ada);
        await world.RenameOwnerFolder("trip", "trip-moved");

        var share = Assert.Single(await world.Door(Ada).Shares());
        Assert.True(share.Broken);
    }

    [Fact]
    public async Task A_unit_authored_at_run_time_cannot_take_the_friend_side_neurons_type()
    {
        await using var world = await World.Start();
        var id = await world.ShareTrip(ShareRole.Viewer);

        // A runtime unit cannot compile/deploy references to the door or silo neuron seam.
        var doorCallerSource = """
            using DigitalBrain.Time;
            namespace Evil.Units;
            [Signal("evil.forged.ran")]
            public sealed record Ran(DateTimeOffset At) : Signal;
            [State("files.shared-with-me")]
            public sealed record ForgedState(int Count = 0) { public ForgedState() : this(0) { } }
            public static class ForgedRelay
            {
                public static Type Leak() => typeof(DigitalBrain.Runtime.Orleans.Hosting.SiloNeurons);

                [Synapse(typeof(Tick), "files.shared-with-me", Every = "every 5m")]
                public static Reaction<ForgedState> Run(Tick tick, ForgedState state)
                    => Reaction<ForgedState>.Of(state with { Count = state.Count + 1 }, new Ran(tick.At));
            }
            """;

        // Even valid unit code that declares [State("files.shared-with-me")] never replaces the compiled relay neuron.
        var impersonatingSource = """
            using DigitalBrain.Time;
            namespace Evil.Units;
            [Signal("evil.impersonate.ran")]
            public sealed record Impersonated(DateTimeOffset At) : Signal;
            [State("files.shared-with-me")]
            public sealed record RelayState(int Count = 0) { public RelayState() : this(0) { } }
            public static class ImpersonatingUnit
            {
                [Synapse(typeof(Tick), "files.shared-with-me", Every = "every 5m")]
                public static Reaction<RelayState> Run(Tick tick, RelayState state)
                    => Reaction<RelayState>.Of(state with { Count = state.Count + 1 }, new Impersonated(tick.At));
            }
            """;

        using (As(Ben))
        {
            var refused = await world.Studio(Ben).Deploy("forged-relay", doorCallerSource);
            Assert.False(refused.Ok);

            _ = await world.Studio(Ben).Deploy("impersonate-relay", impersonatingSource);
            // The compiled SharedWithMeNeuron stays hosted at files.shared-with-me, and ProberNeuron still cannot drive it.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Prober(Ben).ReadThroughRelay(Ada, id, "day1/a.txt"));
            var bytes = await world.Mine(Ben).Read(Ada, id, "day1/a.txt");
            Assert.Equal("hello", Encoding.UTF8.GetString(bytes!));
        }
    }

    // Every silo's edge stamps through the one Orleans ambient, so the edge of the world last started serves them all.
    internal static IDisposable As(string account, string? brain = null, Who kind = Who.Person)
        => World.Edge.Enter(Stamp.Of(World.Edge, kind, new BrainId(brain ?? account), account, app: $"{brain ?? account}/apps/calculator"));

    internal sealed class World(DoorFixture fixture, INeuronRuntime runtime) : IAsyncDisposable
    {
        internal static EdgeAuthority Edge { get; private set; } = null!;

        public static async Task<World> Start(DigitalBrain.Testing.Conformance.Host.OffsetClock? clock = null)
        {
            var fixture = new DoorFixture();
            var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct,
                siloServices: clock is null ? null : DigitalBrain.Testing.Conformance.Host.OrleansRun.ClockServices(clock));
            var world = new World(fixture, runtime);
            Edge = runtime.Edge;
            await world.Seed();
            await world.Befriend(Ada, Ben);
            return world;
        }

        public IFileShares Door(string brain) => SiloNeurons.Get<IFileShares>(runtime.Services, FileSharesDefaults.Id(brain));
        public ISharedWithMe Mine(string brain) => SiloNeurons.Get<ISharedWithMe>(runtime.Services, SharedWithMeDefaults.Key(brain));
        public IFriendLinks Links(string brain) => SiloNeurons.Get<IFriendLinks>(runtime.Services, FriendLinksDefaults.Id(brain));
        public IProfile Profile(string brain) => SiloNeurons.Get<IProfile>(runtime.Services, new NeuronId(new BrainId(brain), "os.profile", "profile"));
        public IUserSettings Settings(string brain) => SiloNeurons.Get<IUserSettings>(runtime.Services, IUserSettings.Key(new BrainId(brain)));
        public ISynapseStudio Studio(string brain) => SiloNeurons.Get<ISynapseStudio>(runtime.Services, ISynapseStudio.Key(brain));

        // One makes a code, the other redeems it.
        public async Task Befriend(string minter, string redeemer)
        {
            FriendCode code;
            using (As(minter)) { code = await Links(minter).Mint(); }
            using (As(redeemer)) { await Links(redeemer).Redeem(code.Code); }
        }

        public IProber Prober(string brain) => SiloNeurons.Get<IProber>(runtime.Services, $"{brain}/outer");

        public async Task<string> ShareTrip(ShareRole role)
        {
            using var ada = As(Ada);
            return (await Door(Ada).Share(Ben, "/work/trip", role)).Id;
        }

        public Task<IReadOnlyList<FileShareServed>> Served()
            => runtime.Signals<FileShareServed>(FileSharesDefaults.Id(Ada), Ct);

        public Task<string> ReadAsOwner(string path)
            => WorkspaceFilesService.ForBrain(runtime.Services, Ada).WithVfs(vfs => vfs.ReadText(path), Ct);

        public Task RenameOwnerFolder(string existingName, string newName)
            => WorkspaceFilesService.ForBrain(runtime.Services, Ada).WithVfs(async vfs =>
            {
                var work = (await vfs.GetMounts(Ct)).Single(mount => mount.MountPoint == SharePath.Mount).Storage;
                var folder = await work.ResolvePath(existingName, Ct) ?? throw new InvalidOperationException($"{existingName} was not found");
                await work.Write(WritePlan.Rename(folder.Id, newName), Ct);
                return true;
            }, Ct);

        // /work/trip/day1/a.txt = "hello" and /work/private/secret.txt, by exact path in Ada's workspace.
        private async Task Seed()
        {
            using var ada = As(Ada);
            await WorkspaceFilesService.ForBrain(runtime.Services, Ada).WithVfs(async vfs =>
            {
                var work = (await vfs.GetMounts(Ct)).Single(mount => mount.MountPoint == SharePath.Mount).Storage;
                var root = (await work.Roots(Ct))[0];
                await work.Write(WritePlan.CreateFolder(root.Id, "trip"), Ct);
                await work.Write(WritePlan.CreateFolder(root.Id, "private"), Ct);
                var trip = await work.ResolvePath("trip", Ct) ?? throw new InvalidOperationException("trip was not created");
                await work.Write(WritePlan.CreateFolder(trip.Id, "day1"), Ct);
                await vfs.Write("/work/trip/day1/a.txt", Encoding.UTF8.GetBytes("hello"), Ct);
                await vfs.Write("/work/private/secret.txt", Encoding.UTF8.GetBytes("secret"), Ct);
                return true;
            }, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await runtime.DisposeAsync();
            await fixture.DisposeAsync();
        }
    }

    // Every brain is owned by the account of the same name.
    private sealed class SameNameOwners : IBrainOwners
    {
        public Task<string?> OwnerAccount(BrainId brain, CancellationToken cancellationToken = default) => Task.FromResult<string?>(brain.Value);
    }

    private sealed class NoConnections : ICapabilityConnections
    {
        public Task<IReadOnlyList<BrainConnection>> ListInOrder(BrainId brain, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BrainConnection>>([]);

        public Task<TCapability> Bind<TCapability>(BrainId brain, string connectionKey, string callerApp, CancellationToken ct = default)
            where TCapability : class
            => throw new NotSupportedException();

        public Task RequestConnection(BrainId brain, CapabilityId capability, string callerApp, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    internal sealed class DoorFixture : BothRuntimesFixture
    {
        public override BrainId Brain { get; } = new(Ada);

        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(IntoChat.OS.Modules.LocalDisk.CloudModule), typeof(ProberModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.Configure<Orleans.Configuration.SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromMinutes(2));
            services.Configure<Orleans.Configuration.ClientMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromMinutes(2));
            services.AddSingleton<IBrainOwners, SameNameOwners>();
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.TryAddSingleton<ICapabilityConnections, NoConnections>();
            services.TryAddSingleton(TimeProvider.System);
        }
    }
}

[Module("test-prober")]
public sealed class ProberModule : DigitalBrain.Modules.Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

[NeuronAlias("test.prober"), DefaultNeuronType("test.prober")]
public interface IProber
{
    Task<byte[]?> ReadThroughRelay(string ownerBrain, string shareId, string path);
    Task<byte[]?> ReadDoorThroughInner(string ownerBrain, string shareId, string path);
    Task<byte[]?> ReadDoor(string ownerBrain, string shareId, string path);
}

// A neuron in the friend's brain that is not a listed screen: what a unit or any other neuron looks like to the relay and the door.
[Neuron("test.prober")]
public sealed class ProberNeuron(IServiceProvider services) : Neuron, IProber
{
    public Task<byte[]?> ReadThroughRelay(string ownerBrain, string shareId, string path)
        => Get<ISharedWithMe>(new NeuronId(Id.Brain, SharedWithMeDefaults.TypeName, SharedWithMeDefaults.KeyPart)).Read(ownerBrain, shareId, path);

    public Task<byte[]?> ReadDoorThroughInner(string ownerBrain, string shareId, string path)
        => Get<IProber>(new NeuronId(Id.Brain, "test.prober", "inner")).ReadDoor(ownerBrain, shareId, path);

    public Task<byte[]?> ReadDoor(string ownerBrain, string shareId, string path)
        => SiloNeurons.Get<IFileShares>(services, FileSharesDefaults.Id(ownerBrain)).Read(shareId, path);
}
