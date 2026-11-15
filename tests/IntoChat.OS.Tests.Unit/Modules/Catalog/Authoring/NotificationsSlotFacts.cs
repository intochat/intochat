using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Notifications;
using IntoChat.OS.Modules.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class NotificationsSlotFacts
{
    private static readonly BrainId Owner = new("owner");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotificationsSlotDeliversThroughRealNotificationsInboxOnTestOS()
    {
        var services = new ServiceCollection();
        services.Configure<NotificationsOptions>(options => options.MaxItems = 200);
        await using var os = TestOS.Local(options => options.Services = services.BuildServiceProvider())
            .Register(typeof(NotificationsModule).Assembly)
            .Register(typeof(AppsModule).Assembly);

        var slot = os.Get<INotifications>(new NeuronId(Owner, NotificationsSlotConnection.TypeName, "btcalert/inbox"));
        var receipt = await slot.Notify("BTC Alert", "BTC crossed $100k", "high", Ct);

        var receiptId = receipt.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(receiptId));

        var inbox = os.Get<INotificationsInbox>(INotificationsInbox.Key(Owner.Value));
        var items = await inbox.ListAsync(cancellationToken: Ct);
        var delivered = Assert.Single(items);
        Assert.Equal(receiptId, delivered.Id);
        Assert.Equal("BTC Alert", delivered.Title);
        Assert.Equal("BTC crossed $100k", delivered.Body);
    }

    [Fact]
    public async Task NotificationsSlotFailsHonestlyWhenInboxMissing()
    {
        await using var os = TestOS.Local()
            .Register(typeof(AppsModule).Assembly);

        var slot = os.Get<INotifications>(new NeuronId(Owner, NotificationsSlotConnection.TypeName, "btcalert/inbox"));

        await Assert.ThrowsAnyAsync<Exception>(() => slot.Notify("BTC Alert", "BTC crossed $100k", "high", Ct));
    }

    [Fact]
    public async Task NotificationsSlotFailsHonestlyWhenDetached()
    {
        var slot = new NotificationsSlotConnection();

        await Assert.ThrowsAsync<InvalidOperationException>(() => slot.Notify("BTC Alert", "BTC crossed $100k", "high", Ct));
    }
}
