using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Views;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Views;

public sealed class ViewPublisherFacts
{
    [Fact]
    public async Task RestartSkipsUnchangedTreeAndContinuesVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var view = brain.Get<IView>("publisher");
        await view.Publish(new(3, 7, Ui.Text("title", "before")));
        var publisher = new ViewPublisher(view, 3);
        await publisher.Publish(Ui.Text("title", "before"));
        Assert.Equal(7, (await view.Read())!.Version);
        await publisher.Publish(Ui.Text("title", "after"));
        Assert.Equal(8, (await view.Read())!.Version);
        var replacement = new ViewPublisher(view, 4);
        await replacement.Publish(Ui.Text("title", "after"));
        Assert.Equal((4L, 1L), ((await view.Read())!.Generation, (await view.Read())!.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ViewPublisher(view, 3).Publish(Ui.Text("title", "stale")));
    }

    [Fact]
    public async Task FailedPublicationDoesNotAdvanceVersionOrPoisonCache()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var view = brain.Get<IView>("retry");
        var publisher = new ViewPublisher(view, 1);
        await Assert.ThrowsAsync<ArgumentException>(() => publisher.Publish(Ui.Column("root", [Ui.Text("same", "a"), Ui.Text("same", "b")])));
        await publisher.Publish(Ui.Text("valid", "ok"));
        Assert.Equal(1, (await view.Read())!.Version);
    }
}
