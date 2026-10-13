using DigitalBrain.Apps;
using DigitalBrain.Flutter.Person;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Collection;
using DigitalBrain.Flame.Scene;
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

    public async Task Rebuild()
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
            List<SpaceComposer.SpaceEntry> entries = [];
            await foreach (var instance in kernel.InstancesIn(brain, kernel.TypeOf(typeof(IApp))))
            {
                try
                {
                    var app = await grains.GetGrain<IApp>(instance.Key).Read();
                    entries.Add(new(instance.Key, app.Manifest.Title, app.Manifest.Description, app.Presentation, app.Manifest.Toolbar ?? []));
                }
                catch (Exception error) { logger.LogWarning(error, "Could not read {App} while rebuilding space {Brain}.", instance.Key, brain); }
            }
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
}

internal sealed class SpaceActivation : IActivationHandler<SpaceNeuron>
{
    public Task Activated(SpaceNeuron neuron, CancellationToken cancellationToken) => neuron.Rebuild();
}
