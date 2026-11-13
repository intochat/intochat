using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Notifications;

public sealed class NotificationsNeuronFacts
{
    private static readonly BrainId Owner = new("owner");
    private static readonly NeuronId InboxId = INotificationsInbox.Key(Owner.Value);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TestOS CreateOs(int maxItems = 200)
    {
        var services = new ServiceCollection();
        services.Configure<NotificationsOptions>(options => options.MaxItems = maxItems);
        return TestOS.Local(options => options.Services = services.BuildServiceProvider())
            .Register(typeof(NotificationsModule).Assembly);
    }

    [Fact]
    public async Task SendAndNotifyPersistItemsEmitSignalAndDeriveTrustedSourceApp()
    {
        await using var os = CreateOs();
        var inbox = os.Get<INotificationsInbox>(InboxId);

        NotificationReceipt appReceipt;
        using (os.Edge.Enter(os.Edge.App("owner/apps/tasks", new Actor.Person("a1", "owner"), Owner)))
        {
            appReceipt = await inbox.SendAsync(
                new NotificationRequest("Task finished", "Build #42 passed", NotificationPriority.High, Category: "build", ActionUrl: "app://tasks/42"),
                Ct);
        }

        NotificationReceipt userReceipt;
        using (RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", Owner.Value)))
        {
            userReceipt = await inbox.NotifyAsync("Reminder", "Standup in 5m", NotificationPriority.Normal, Ct);
        }

        Assert.False(appReceipt.Deduplicated);
        Assert.False(userReceipt.Deduplicated);
        Assert.Equal(2, await inbox.UnreadCountAsync(Ct));

        var items = await inbox.ListAsync(cancellationToken: Ct);
        Assert.Equal(2, items.Length);
        Assert.Equal(userReceipt.Id, items[0].Id);
        Assert.Equal("user", items[0].SourceApp);
        Assert.Equal(appReceipt.Id, items[1].Id);
        Assert.Equal("owner/apps/tasks", items[1].SourceApp);
        Assert.Equal("build", items[1].Category);
        Assert.Equal("app://tasks/42", items[1].ActionUrl);
        Assert.False(items[1].Read);

        var signals = os.Journal(Owner).Signals(InboxId)
            .Select(entry => entry.Signal)
            .OfType<NotificationSent>()
            .ToArray();
        Assert.Equal(2, signals.Length);
        Assert.Equal(appReceipt.Id, signals[0].NotificationId);
        Assert.Equal("owner/apps/tasks", signals[0].SourceApp);
        Assert.Equal(NotificationPriority.High, signals[0].Priority);
        Assert.Equal(userReceipt.Id, signals[1].NotificationId);
    }

    [Fact]
    public async Task SendDeduplicatesByDedupeKeyWithoutEmittingDuplicateSignal()
    {
        await using var os = CreateOs();
        var inbox = os.Get<INotificationsInbox>(InboxId);

        var first = await inbox.SendAsync(new NotificationRequest("Sync error", "Token expired", NotificationPriority.Urgent, DedupeKey: "sync:token"), Ct);
        var second = await inbox.SendAsync(new NotificationRequest("Sync error", "Token expired again", NotificationPriority.Urgent, DedupeKey: "sync:token"), Ct);

        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Single(await inbox.ListAsync(cancellationToken: Ct));
        Assert.Single(os.Journal(Owner).Signals(InboxId).Select(entry => entry.Signal).OfType<NotificationSent>());
    }

    [Fact]
    public async Task MarkReadMarkAllReadAndClearUpdateStateAndUnreadCount()
    {
        await using var os = CreateOs();
        var inbox = os.Get<INotificationsInbox>(InboxId);

        var first = await inbox.NotifyAsync("First", "One", cancellationToken: Ct);
        var second = await inbox.NotifyAsync("Second", "Two", cancellationToken: Ct);
        var third = await inbox.NotifyAsync("Third", "Three", cancellationToken: Ct);
        Assert.Equal(3, await inbox.UnreadCountAsync(Ct));

        await inbox.MarkReadAsync(second.Id, Ct);
        Assert.Equal(2, await inbox.UnreadCountAsync(Ct));
        var unread = await inbox.ListAsync(unreadOnly: true, cancellationToken: Ct);
        Assert.Equal([third.Id, first.Id], unread.Select(item => item.Id).ToArray());

        var allAfterSingleRead = await inbox.ListAsync(cancellationToken: Ct);
        var marked = Assert.Single(allAfterSingleRead, item => item.Id == second.Id);
        Assert.True(marked.Read);
        Assert.NotNull(marked.ReadUtc);

        await inbox.MarkAllReadAsync(Ct);
        Assert.Equal(0, await inbox.UnreadCountAsync(Ct));
        Assert.Empty(await inbox.ListAsync(unreadOnly: true, cancellationToken: Ct));
        Assert.All(await inbox.ListAsync(cancellationToken: Ct), item => Assert.True(item.Read));

        await inbox.ClearAsync(Ct);
        Assert.Empty(await inbox.ListAsync(cancellationToken: Ct));
        Assert.Equal(0, await inbox.UnreadCountAsync(Ct));
    }

    [Fact]
    public async Task MaxItemsBoundsInboxByTrimmingOldestItems()
    {
        await using var os = CreateOs(maxItems: 3);
        var inbox = os.Get<INotificationsInbox>(InboxId);

        var r1 = await inbox.NotifyAsync("One", "1", cancellationToken: Ct);
        var r2 = await inbox.NotifyAsync("Two", "2", cancellationToken: Ct);
        var r3 = await inbox.NotifyAsync("Three", "3", cancellationToken: Ct);
        var r4 = await inbox.NotifyAsync("Four", "4", cancellationToken: Ct);

        var items = await inbox.ListAsync(limit: 10, cancellationToken: Ct);
        Assert.Equal(3, items.Length);
        Assert.Equal([r4.Id, r3.Id, r2.Id], items.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(items, item => item.Id == r1.Id);
        Assert.Equal(3, await inbox.UnreadCountAsync(Ct));
    }
}

public sealed class NotificationsModuleOnTestOS : ModuleOnTestOSFacts<NotificationsModule>;
