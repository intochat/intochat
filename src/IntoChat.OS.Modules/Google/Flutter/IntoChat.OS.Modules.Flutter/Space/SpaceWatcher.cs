using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Modules.Circles.Signals;
using IntoChat.OS.Modules.Flutter.Card.Signals;
using IntoChat.OS.Modules.Flutter.Person.Signals;
using IntoChat.OS.Modules.Flutter.Workspace.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using System.Collections.Concurrent;
using DigitalBrain.Runtime.Orleans.Signalling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter.Space;

// Rebuilds spaces when what they show changes. Signals of plain publishers (Card, Person; 06-04) arrive through IBrainListeners,
// per brain: a brain is followed when its Space activates on this silo, together with its friends, whose posts and profiles it
// shows. Signals of still-legacy publishers stay on the local hub until they migrate (SpaceWatcherLegacyHubSignals.json).
internal sealed class SpaceWatcher(LocalSignalHub hub, IGrainFactory grains, ILogger<SpaceWatcher> logger, CircleMembers friends,
    IBrainListeners listeners, DigitalBrain.Spi.IClusterListeners cluster, IntoChat.OS.Runtime.IPlainNeurons plain) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> _followed = new(StringComparer.Ordinal);

    private readonly LocalSignalHub.Subscription<PresentationChanged> _presentation = hub.Subscribe<PresentationChanged>();
    private readonly LocalSignalHub.Subscription<AppChanged> _apps = hub.Subscribe<AppChanged>();

    private readonly LocalSignalHub.Subscription<CircleChanged> _circles = hub.Subscribe<CircleChanged>();
    private readonly LocalSignalHub.Subscription<CircleMembershipRevoked> _revoked = hub.Subscribe<CircleMembershipRevoked>();
    private readonly LocalSignalHub.Subscription<CircleSpoke> _posts = hub.Subscribe<CircleSpoke>();

    // Workspace changes come from plain workspaces of every brain this silo hosts (IClusterListeners, R4-20).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var workspaces = await cluster.Listen<WorkspaceChanged>(nameof(SpaceWatcher), (_, change, _) => Guarded(change, change => Rebuild(change.WorkspaceId)));
        await Consume(stoppingToken);
    }

    private Task Consume(CancellationToken stoppingToken) => Task.WhenAll(
        Consume(_apps, change => RefreshApp(change.Publisher.Name[(change.Publisher.Name.IndexOf('/') + 1)..]), stoppingToken),
        Consume(_revoked, change => RebuildMembers(change.PreviousMembers), stoppingToken),
        Consume(_circles, change => RebuildMembers(change.Members.Concat(change.PreviousMembers ?? [])), stoppingToken),
        Consume(_posts, async change =>
        {
            var key = change.Publisher.Name[(change.Publisher.Name.IndexOf('/') + 1)..];
            var circle = await grains.GetGrain<ICircle>(ICircle.Key(key[..^5])).State();
            await RebuildMembers(circle.Members);
        }, stoppingToken),
        Consume(_presentation, change => RefreshApp(change.App), stoppingToken));

    // Follows brain and its friends on this silo, once each: their profile and post changes rebuild the spaces that show them.
    public async Task Follow(string brain)
    {
        if (!BrainScope.IsValidId(brain)) { return; }
        await Listen(brain);
        foreach (var friend in await friends.FriendsOf(brain)) { await Listen(friend.Brain); }
    }

    private Task Listen(string brain) => _followed.GetOrAdd(brain, async key =>
    {
        var id = new DigitalBrain.BrainId(key);
        await listeners.Listen<PersonChanged>(id, (change, _) => Guarded(change, change => RebuildAuthorAndFriends(change.Name.EndsWith("/profile", StringComparison.Ordinal) ? change.Name[..^8] : null)));
        await listeners.Listen<CardChanged>(id, (change, _) => Guarded(change, change => RebuildAuthorAndFriends(CircleMembers.PostBrain(change.Name))));
    });

    private async Task Guarded<T>(T signal, Func<T, Task> handle)
    {
        try { await handle(signal); }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogWarning(error, "Could not rebuild a space after {Signal}.", typeof(T).Name); }
    }

    private async Task Consume<T>(LocalSignalHub.Subscription<T> subscription, Func<T, Task> handle, CancellationToken ct) where T : Signal
    {
        using (subscription)
        {
            await foreach (var signal in subscription.Reader.ReadAllAsync(ct))
            {
                try { await handle(signal); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Could not rebuild a space after {Signal}.", typeof(T).Name); }
            }
        }
    }

    private async Task RebuildAuthorAndFriends(string? author)
    {
        if (!BrainScope.IsValidId(author)) { return; }
        await Rebuild(author);
        foreach (var friend in await friends.FriendsOf(author)) { await Rebuild(friend.Brain); }
    }

    private async Task RebuildMembers(IEnumerable<string> accounts)
    {
        foreach (var account in accounts.Distinct(StringComparer.Ordinal))
        { foreach (var brain in await friends.PersonalBrains(account)) { await Rebuild(brain); } }
    }

    private Task Rebuild(string? brain) => brain is null ? Task.CompletedTask : Unstamped(() => plain.Get<ISpace>(ISpace.Key(brain)).Recompose());
    private Task RefreshApp(string key) => BrainOf(key) is { } brain ? Unstamped(() => plain.Get<ISpace>(ISpace.Key(brain)).RefreshApp(key)) : Task.CompletedTask;

    // A recomposition is the module's own act. A plain listener runs in the turn that saved the change, with that writer's stamp,
    // and a friend's change must not reach another brain's space as the friend: the call goes out unstamped, as the hub pump's did.
    private static async Task Unstamped(Func<Task> call)
    {
        global::Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey);
        await call();
    }
    private static string? BrainOf(string key)
    {
        var marker = key.IndexOf("/apps/", StringComparison.Ordinal);
        return marker > 0 ? key[..marker] : null;
    }
}
