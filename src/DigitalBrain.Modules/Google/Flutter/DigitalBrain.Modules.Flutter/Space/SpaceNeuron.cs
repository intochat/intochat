using DigitalBrain.Apps;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flutter.Person;
using DigitalBrain.Flutter.Workspace;
using DigitalBrain.Framework;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace DigitalBrain.Flutter.Space;

[GrainType("ui.space")]
internal sealed class SpaceNeuron(IGrainFactory grains, IKernel kernel, CompiledTypes compiled, ILogger<SpaceNeuron> logger, CircleMembers friends) : Neuron, ISpace
{
    public Task<PersonState> ReadProfile(string key) => ReadShared(async () =>
    {
        if (!key.EndsWith("/profile", StringComparison.Ordinal)) { throw new UnauthorizedAccessException(); }
        await AuthorizeFriend(key[..^8]);
        return await grains.GetGrain<ISharedPersonReader>(key).Read();
    });

    public Task<CardState> ReadPost(string key) => ReadShared(async () =>
    {
        var author = CircleMembers.PostBrain(key) ?? throw new UnauthorizedAccessException();
        var friend = await AuthorizeFriend(author);
        if (friend is not null && !(await friends.Posts(friend)).Contains(key, StringComparer.Ordinal))
        { throw new UnauthorizedAccessException("The post is not published in a shared circle."); }
        var card = await grains.GetGrain<ISharedCardReader>(key).Read();
        return new CardState { Name = key, Version = card.Version, Title = card.Title, Body = card.Body };
    });

    public Task<CollectionState> ReadPosts(string key) => ReadShared(async () =>
    {
        if (!key.EndsWith("/posts", StringComparison.Ordinal)) { throw new UnauthorizedAccessException(); }
        var friend = await AuthorizeFriend(key[..^6]);
        if (friend is null) { return await grains.GetGrain<ICollectionView>(key).Read(); }
        List<CollectionItem> items = [];
        foreach (var post in await friends.Posts(friend))
        { items.Add(new(post, (await grains.GetGrain<ISharedCardReader>(post).Read()).Title, "post")); }
        return new CollectionState { Name = key, Revision = 1, Definition = new(items.ToArray(), FileMode: false, SelectionEnabled: false, EmptyLabel: "No posts yet") };
    });

    private async Task<CircleMembers.Friend?> AuthorizeFriend(string author)
    {
        var viewer = this.GetPrimaryKeyString();
        if (author == viewer) { return null; }
        return (await friends.FriendsOf(viewer)).SingleOrDefault(friend => friend.Brain == author)
            ?? throw new UnauthorizedAccessException("The author is not a current circle friend.");
    }

    private async Task<T> ReadShared<T>(Func<Task<T>> read)
    {
        var caller = CallerContextStamper.Require();
        if (BrainScope.Create(caller.AccountId, caller.BrainId).Id != this.GetPrimaryKeyString())
        { throw new UnauthorizedAccessException("Read Friends through the viewer's space."); }
        // Keep the verified viewer caller on every call; guarded foreign leaves use typed module readers.
        return await read();
    }

    private bool _rebuilding, _rebuildPending, _fullRefresh;
    private readonly HashSet<string> _dirtyApps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SpaceComposer.SpaceEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);

    public Task RefreshApp(string key)
    {
        if (!key.StartsWith(this.GetPrimaryKeyString() + "/apps/", StringComparison.Ordinal))
        { throw new UnauthorizedAccessException("An app must belong to this space."); }
        _dirtyApps.Add(key);
        return Recompose();
    }

    public Task Rebuild()
    {
        _fullRefresh = true;
        return Recompose();
    }

    // Invalidations may arrive while the scan awaits another grain. They share
    // one worker; snapshot pending keys before awaits so later changes are retained.
    public async Task Recompose()
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
                var dirtyApps = _dirtyApps.ToArray();
                _fullRefresh = false;
                _dirtyApps.Clear();
                try { await RebuildOnce(fullRefresh, dirtyApps); }
                catch (Exception error)
                {
                    _fullRefresh |= fullRefresh;
                    _dirtyApps.UnionWith(dirtyApps);
                    if (!_rebuildPending) { throw; }
                    // An accepted invalidation must survive a failed scan even
                    // if no later event arrives to start another worker.
                    logger.LogWarning(error, "Space {Brain} scan failed; rebuilding its pending invalidation.", this.GetPrimaryKeyString());
                }
            }
            while (_rebuildPending);
        }
        finally { _rebuilding = false; }
    }

    private async Task RebuildOnce(bool fullRefresh, string[] dirtyApps)
    {
        var brain = this.GetPrimaryKeyString();
        var types = compiled.Entries;
        if (!types.Any(type => type.Name == typeof(IApp).FullName) || !types.Any(type => type.Name == typeof(IScene).FullName)) { return; }
        // This is the module's derived write, even when an app's Show caused the invalidation.
        var inherited = RequestContext.Get(CallerContextStamper.RequestContextKey);
        RequestContext.Remove(CallerContextStamper.RequestContextKey);
        try
        {
            var workspace = await grains.GetGrain<IWorkspace>(brain).Read();
            if (fullRefresh || dirtyApps.Length > 0)
            {
                // Membership is authoritative: a delayed presentation must not
                // resurrect a deleted app, and deletion must remove its cached entry.
                HashSet<string> members = new(StringComparer.Ordinal);
                await foreach (var instance in kernel.InstancesIn(brain, kernel.TypeOf(typeof(IApp))))
                { members.Add(instance.Key); }
                foreach (var key in _entries.Keys.Where(key => !members.Contains(key)).ToArray())
                { _entries.Remove(key); }
                foreach (var key in _aliases.Keys.Where(key => !members.Contains(key)).ToArray())
                { _aliases.Remove(key); }
                var refresh = fullRefresh ? members : members.Where(key => (!_entries.ContainsKey(key) && !_aliases.ContainsKey(key)) || dirtyApps.Contains(key, StringComparer.Ordinal));
                foreach (var key in refresh)
                {
                    try
                    {
                        var app = await grains.GetGrain<IApp>(key).Read();
                        if (app.MigrationTarget is { } target)
                        {
                            _entries.Remove(key);
                            _aliases[key] = target;
                            continue;
                        }
                        _aliases.Remove(key);
                        _entries[key] = new(key, app.Manifest.Title, app.Manifest.Description, app.Presentation, app.Manifest.Toolbar ?? []);
                    }
                    catch (Exception error)
                    {
                        _entries.Remove(key);
                        _aliases.Remove(key);
                        // Retry transient failures on the next event, without a hot retry loop.
                        _dirtyApps.Add(key);
                        logger.LogWarning(error, "Could not read {App} while rebuilding space {Brain}.", key, brain);
                    }
                }
            }
            // Resolve presentation only; retained workspace paths and migration source data stay intact.
            workspace = workspace with { Path = workspace.Path.Select(ResolveAlias).ToArray() };
            List<SpaceComposer.SpaceEntry> entries = [.. _entries.Values];
            if (types.Any(type => type.Name == typeof(DigitalBrain.Circles.ICircle).FullName))
            { entries.AddRange(await friends.Entries(brain, workspace.Path[^1])); }
            var scene = grains.GetGrain<IScene>(brain + "/space");
            var current = await scene.Read();
            await scene.Set(SpaceComposer.Compose(workspace, [.. entries]), current.Revision);
        }
        finally
        {
            if (inherited is not null) { RequestContext.Set(CallerContextStamper.RequestContextKey, inherited); }
        }
    }

    private string ResolveAlias(string key)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (visited.Add(key) && _aliases.TryGetValue(key, out var target)) { key = target; }
        return key;
    }
}

internal sealed class SpaceActivation : IActivationHandler<SpaceNeuron>
{
    public Task Activated(SpaceNeuron neuron, CancellationToken cancellationToken) => neuron.Rebuild();
}
