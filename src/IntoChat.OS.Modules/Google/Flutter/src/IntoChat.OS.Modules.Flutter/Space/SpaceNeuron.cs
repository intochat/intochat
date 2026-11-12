using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flame.Scene;
using IntoChat.OS.Modules.Flutter.Workspace;
using Microsoft.Extensions.Logging;
using MicrosoftSdk = Microsoft;

namespace IntoChat.OS.Modules.Flutter.Space;

[Neuron("ui.space")]
internal sealed class SpaceNeuron(IServiceProvider plain) : DigitalBrain.Neurons.Neuron<SpaceRecords>, ISpace, ISpaceRecords
{
    // Resolved when used, so the Space's own records need none of the composition services.
    private IKernel Kernel => MicrosoftSdk.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IKernel>(plain);
    private CompiledTypes Compiled => MicrosoftSdk.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CompiledTypes>(plain);
    private ILogger<SpaceNeuron> Logger => MicrosoftSdk.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ILogger<SpaceNeuron>>(plain);
    private CircleMembers Friends => MicrosoftSdk.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<CircleMembers>(plain);

    public Task<PersonState> ReadProfile(string key) => ReadShared(async () =>
    {
        if (!key.EndsWith("/profile", StringComparison.Ordinal)) { throw new UnauthorizedAccessException(); }
        await AuthorizeFriend(key[..^8]);
        return (await CircleMembers.RecordsOf(plain, key[..^8])).Profile;
    });

    public Task<CardState> ReadPost(string key) => ReadShared(async () =>
    {
        var author = CircleMembers.PostBrain(key) ?? throw new UnauthorizedAccessException();
        var friend = await AuthorizeFriend(author);
        if (friend is not null && !(await Friends.Posts(friend)).Contains(key, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException("The post is not published in a shared circle."); }
        var card = CircleMembers.Post(await CircleMembers.RecordsOf(plain, author), key);
        return new CardState { Name = key, Version = card.Version, Title = card.Title, Body = card.Body };
    });

    public Task<CollectionState> ReadPosts(string key) => ReadShared(async () =>
    {
        if (!key.EndsWith("/posts", StringComparison.Ordinal)) { throw new UnauthorizedAccessException(); }
        var author = key[..^6];
        var friend = await AuthorizeFriend(author);
        var records = await CircleMembers.RecordsOf(plain, author);
        var keys = friend is null ? records.Posts.Select(post => post.Name).ToArray() : await Friends.Posts(friend);
        CollectionItem[] items = [.. keys.Select(post => new CollectionItem(post, CircleMembers.Post(records, post).Title, "post"))];
        return new CollectionState { Name = key, Revision = 1, Definition = new(items, FileMode: false, SelectionEnabled: false, EmptyLabel: "No posts yet") };
    });

    // This brain's Space records: its profile and posts.
    public Task<SpaceRecords> Records() => Task.FromResult(State);

    public Task SetProfile(string displayName, string avatarUrl) => Write(records =>
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(avatarUrl);
        var key = Id.Brain.Value + "/profile";
        records.Profile = new PersonState { Name = key, Version = records.Profile.Version + 1, DisplayName = displayName.Trim(), AvatarUrl = avatarUrl.Trim() };
    });

    public Task Post(string id, string title, string body) => Write(records =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(body);
        var key = Id.Brain.Value + "/posts/" + id;
        if (CircleMembers.PostBrain(key) != Id.Brain.Value) { throw new ArgumentException("A post id is letters, digits, '-' and '_'.", nameof(id)); }
        var prior = records.Posts.FirstOrDefault(post => post.Name == key);
        records.Posts = [.. records.Posts.Where(post => post.Name != key), new CardState { Name = key, Version = (prior?.Version ?? 0) + 1, Title = title, Body = body }];
    });

    // Only the brain's own person changes its records; the change rebuilds the spaces that show them.
    private async Task Write(Action<SpaceRecords> change)
    {
        var caller = RuntimeCallerContext.Require();
        if (caller.BrainId != Id.Brain.Value || caller.Kind != DigitalBrain.Capabilities.CallerKind.User)
        { throw new UnauthorizedAccessException("Only the brain's own person changes its profile and posts."); }
        var records = State;
        change(records);
        await Save(records, new SpaceRecordsChanged(Id.Brain.Value));
    }

    private async Task<CircleMembers.Friend?> AuthorizeFriend(string author)
    {
        var viewer = Id.Brain.Value;
        if (author == viewer) { return null; }
        return (await Friends.FriendsOf(viewer)).SingleOrDefault(friend => friend.Brain == author)
            ?? throw new UnauthorizedAccessException("The author is not a current circle friend.");
    }

    private async Task<T> ReadShared<T>(Func<Task<T>> read)
    {
        var caller = RuntimeCallerContext.Require();
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != Id.Brain.Value)
        { throw new UnauthorizedAccessException("Read Friends through the viewer's space."); }
        // Keep the verified viewer caller on every call; guarded foreign leaves use typed module readers.
        return await read();
    }

    private bool _rebuilding, _rebuildPending, _fullRefresh, _sourcesStale, _friendsFresh;
    private string? _friendFocus;
    private WorkspaceState? _workspace;
    private SpaceComposer.SpaceEntry[] _friendEntries = [];
    private readonly HashSet<string> _dirtyApps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpaceComposer.SpaceEntry> _entries = new(StringComparer.Ordinal);

    public Task RefreshApp(string key)
    {
        if (!key.StartsWith(Id.Brain.Value + "/apps/", StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("An app must belong to this space."); }
        _dirtyApps.Add(key);
        return Schedule();
    }

    public Task Rebuild()
    {
        _fullRefresh = true;
        _sourcesStale = true;
        return Schedule();
    }

    // Workspace, circle and profile changes compose again from those sources. They do not
    // rescan every app. A single app refresh keeps the last workspace and friend entries.
    public Task Recompose()
    {
        _sourcesStale = true;
        return Schedule();
    }

    // Invalidations may arrive while the scan awaits another grain. They share
    // one worker; snapshot pending keys before awaits so later changes are retained.
    private async Task Schedule()
    {
        _rebuildPending = true;
        if (_rebuilding) { return; }
        _rebuilding = true;
        try
        {
            do
            {
                _rebuildPending = false;
                var fullRefresh = _fullRefresh;
                var sourcesStale = _sourcesStale;
                var dirtyApps = _dirtyApps.ToArray();
                _fullRefresh = false;
                _sourcesStale = false;
                _dirtyApps.Clear();
                try { await RebuildOnce(fullRefresh, sourcesStale, dirtyApps); }
                catch (Exception error)
                {
                    _fullRefresh |= fullRefresh;
                    _sourcesStale |= sourcesStale;
                    _dirtyApps.UnionWith(dirtyApps);
                    if (!_rebuildPending) { throw; }
                    // An accepted invalidation must survive a failed scan even
                    // if no later event arrives to start another worker.
                    Logger.LogWarning(error, "Space {Brain} scan failed; rebuilding its pending invalidation.", Id.Brain.Value);
                }
            }
            while (_rebuildPending);
        }
        finally { _rebuilding = false; }
    }

    private async Task RebuildOnce(bool fullRefresh, bool sourcesStale, string[] dirtyApps)
    {
        var brain = Id.Brain.Value;
        var types = Compiled.Entries;
        if (!types.Any(type => type.Name == typeof(IApp).FullName) || !types.Any(type => type.Name == typeof(IScene).FullName)) { return; }
        // This is the module's derived write, even when an app's Show caused the invalidation.
        var inherited = Orleans.Runtime.RequestContext.Get(RuntimeCallerContext.RequestContextKey);
        Orleans.Runtime.RequestContext.Remove(RuntimeCallerContext.RequestContextKey);
        try
        {
            // A presentation change rewrites one app. Workspace, circle and profile changes
            // mark the sources stale and read them again. Membership is a full rebuild.
#pragma warning disable DB0033
            if (fullRefresh || sourcesStale || _workspace is null) { _workspace = await Get<IWorkspace>(new DigitalBrain.NeuronId(Id.Brain, IWorkspace.Type, "main")).Read(); }
#pragma warning restore DB0033
            var held = _workspace ?? throw new InvalidOperationException("The space has no workspace.");
            if (fullRefresh)
            {
                HashSet<string> members = new(StringComparer.Ordinal);
                await foreach (var instance in Kernel.InstancesIn(brain, Kernel.TypeOf(typeof(IApp))))
                { members.Add(instance.Key); }
                foreach (var key in _entries.Keys.Where(key => !members.Contains(key)).ToArray())
                { _entries.Remove(key); }
                foreach (var key in members) { await ReadApp(brain, key); }
            }
            else if (dirtyApps.Length > 0)
            {
                // Membership is what removes a deleted app. Its grain can still be read after it leaves the directory.
                HashSet<string> members = new(StringComparer.Ordinal);
                await foreach (var instance in Kernel.InstancesIn(brain, Kernel.TypeOf(typeof(IApp))))
                { members.Add(instance.Key); }
                foreach (var key in _entries.Keys.Where(key => !members.Contains(key)).ToArray())
                { _entries.Remove(key); }
                // A dirty app the directory does not list yet is read too: a plain app announces its first change before the kernel's
                // directory registers it, and its deletion before the directory retires it (06-08). ReadApp drops one that reads as
                // never created.
                foreach (var key in dirtyApps)
                {
                    if (members.Contains(key) || IApp.NameOf(brain, key) is not null) { await ReadApp(brain, key); }
                }
            }
            var workspace = held;
            var focus = workspace.Path[^1];
            if (fullRefresh || sourcesStale || !_friendsFresh || focus != _friendFocus)
            {
                _friendEntries = types.Any(type => type.Name == typeof(IntoChat.OS.Modules.Circles.ICircle).FullName)
                    ? await Friends.Entries(brain, focus) : [];
                _friendFocus = focus;
                _friendsFresh = true;
            }
            SpaceComposer.SpaceEntry[] entries = [.. _entries.Values, .. _friendEntries];
            // The scene is a plain neuron (06-06a), reached by its os key.
            var scene = SiloNeurons.Get<IScene>(plain, brain + "/space");
            var current = await scene.Read();
            // Always publish. A refresh whose picture matches still moves the revision, which is
            // how a caller knows the composition finished. The cost that repeats is the source reads above.
            await scene.Set(SpaceComposer.Compose(workspace, entries), current.Revision);
        }
        finally
        {
            if (inherited is not null) { Orleans.Runtime.RequestContext.Set(RuntimeCallerContext.RequestContextKey, inherited); }
        }
    }

    private async Task ReadApp(string brain, string key)
    {
        try
        {
            var app = await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, key[(brain.Length + 1)..])).Read();
            // A deleted app answers as never created (no title) until the directory retires it.
            if (app.Manifest.Title.Length == 0)
            {
                _entries.Remove(key);
                return;
            }
            _entries[key] = new(key, app.Manifest.Title, app.Manifest.Description, app.Presentation, app.Manifest.Toolbar ?? []);
        }
        catch (Exception error)
        {
            _entries.Remove(key);
            // Retry transient failures on the next event, without a hot retry loop.
            _dirtyApps.Add(key);
            Logger.LogWarning(error, "Could not read {App} while rebuilding space {Brain}.", key, brain);
        }
    }

    // On every activation: the watcher (a hosted service; a host without it follows nothing) follows this brain and its friends,
    // then the space composes once.
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        var watcher = plain.GetService(typeof(IEnumerable<MicrosoftSdk.Extensions.Hosting.IHostedService>)) is IEnumerable<MicrosoftSdk.Extensions.Hosting.IHostedService> hosted
            ? hosted.OfType<SpaceWatcher>().FirstOrDefault() : null;
        if (watcher is not null) { await watcher.Follow(Id.Brain.Value); }
        // A host without the composition services (CompiledTypes) composes no scene; the Space's own records still serve.
        if (plain.GetService(typeof(CompiledTypes)) is not null) { await Rebuild(); }
    }
}
