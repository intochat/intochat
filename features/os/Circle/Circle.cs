using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Circles;
using IntoChat.OS.Client;
using DigitalBrain;

[App("circle")]
public sealed class CircleApp : DigitalBrain.App
{
    [On("open")]
    public async Task Open(string? input)
    {
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        await CircleView.Compose(brain);
        if (string.IsNullOrWhiteSpace(input))
        {
            await CircleView.Restore(brain);
        }
        else
        {
            await CircleView.Navigate(brain, PublicationUi.Decode<PublicationUi.Navigation>(input));
        }
    }

    [On("create")]
    public async Task Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return; }
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        var name = input.Trim();
        try
        {
            var circle = await CircleView.Factory(brain).Create(name, Guid.NewGuid().ToString());
            await CircleView.Project(brain);
            await CircleView.Status(brain, "Created " + name + " (" + circle + ").");
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await CircleView.Fail(brain, error);
        }
    }

    [On("navigate")]
    public async Task Navigate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return; }
        await using var brain = await IntoChatOSClient.ConnectAsync([]);
        try
        {
            await CircleView.Navigate(brain, PublicationUi.Decode<PublicationUi.Navigation>(input));
        }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await CircleView.Fail(brain, error);
        }
    }
}
