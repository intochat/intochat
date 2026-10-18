using IntoChat.OS.Apps;
using IntoChat.OS.Client;

// "create": a new circle brain through the factory, the only way a script creates one; the index is rebuilt after it.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(CircleView.Key(brain));
await foreach (var request in brain.On(app, "create", brain.Stopping))
{
    var name = request.Input.Trim();
    try
    {
        var circle = await CircleView.Factory(brain).Create(name, request.RequestId.ToString());
        await CircleView.Project(brain);
        await CircleView.Status(brain, "Created " + name + " (" + circle + ").");
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Fail(brain, error); }
}
