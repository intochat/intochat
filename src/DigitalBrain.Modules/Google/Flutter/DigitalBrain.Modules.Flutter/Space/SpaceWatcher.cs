using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Circles;
using DigitalBrain.Circles.Signals;
using DigitalBrain.Flutter.Card.Signals;
using DigitalBrain.Flutter.Person.Signals;
using DigitalBrain.Flutter.Workspace.Signals;
using DigitalBrain.Framework;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Flutter.Space;

internal sealed class SpaceWatcher(LocalSignalHub hub, IGrainFactory grains, ILogger<SpaceWatcher> logger, CircleMembers friends) : BackgroundService
{
    private readonly LocalSignalHub.Subscription<WorkspaceChanged> _workspace = hub.Subscribe<WorkspaceChanged>();
    private readonly LocalSignalHub.Subscription<PresentationChanged> _presentation = hub.Subscribe<PresentationChanged>();
    private readonly LocalSignalHub.Subscription<AppActivity> _activity = hub.Subscribe<AppActivity>();
    private readonly LocalSignalHub.Subscription<AppChanged> _apps = hub.Subscribe<AppChanged>();

    private readonly LocalSignalHub.Subscription<CircleChanged> _circles = hub.Subscribe<CircleChanged>();
    private readonly LocalSignalHub.Subscription<CircleMembershipRevoked> _revoked = hub.Subscribe<CircleMembershipRevoked>();
    private readonly LocalSignalHub.Subscription<CircleSpoke> _posts = hub.Subscribe<CircleSpoke>();

    private readonly LocalSignalHub.Subscription<PersonChanged> _profiles = hub.Subscribe<PersonChanged>();
    private readonly LocalSignalHub.Subscription<CardChanged> _cards = hub.Subscribe<CardChanged>();

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Consume(_apps, change => Rebuild(BrainOf(change.Publisher.Name[(change.Publisher.Name.IndexOf('/') + 1)..])), stoppingToken),
        Consume(_profiles, change => RebuildAuthorAndFriends(change.Name.EndsWith("/profile", StringComparison.Ordinal) ? change.Name[..^8] : null), stoppingToken),
        Consume(_cards, change => RebuildAuthorAndFriends(CircleMembers.PostBrain(change.Name)), stoppingToken),
        Consume(_revoked, change => RebuildMembers(change.PreviousMembers), stoppingToken),
        Consume(_circles, change => RebuildMembers(change.Members.Concat(change.PreviousMembers ?? [])), stoppingToken),
        Consume(_posts, async change =>
        {
            var key = change.Publisher.Name[(change.Publisher.Name.IndexOf('/') + 1)..];
            var circle = await grains.GetGrain<ICircle>(ICircle.Key(key[..^5])).State();
            await RebuildMembers(circle.Members);
        }, stoppingToken),
        Consume(_workspace, change => Rebuild(change.WorkspaceId), stoppingToken),
        Consume(_presentation, change => Rebuild(BrainOf(change.App)), stoppingToken),
        Consume(_activity, async activity =>
        {
            var publisher = activity.Publisher.Name;
            var key = publisher[(publisher.IndexOf('/') + 1)..];
            var brain = BrainOf(key);
            if (brain is null) { return; }
            var app = grains.GetGrain<IApp>(key);
            await app.Stamp("running", activity.State is "working" or "reading" or "searching" or "listening" ? "true" : "false");
            await app.Stamp("attention", activity.State == "attention" ? "true" : "false");
            await Rebuild(brain);
        }, stoppingToken));

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

    private Task Rebuild(string? brain) => brain is null ? Task.CompletedTask : grains.GetGrain<ISpace>(brain).Rebuild();
    private static string? BrainOf(string key)
    {
        var marker = key.IndexOf("/apps/", StringComparison.Ordinal);
        return marker > 0 ? key[..marker] : null;
    }
}
