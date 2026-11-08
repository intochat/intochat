using DigitalBrain.UI;
using DigitalBrain.Client;
using IntoChat.OS.Modules.Flutter.Views;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Sandbox;

// The presenter an [App] class is built with: what it presents is published to the app's view neurons, main at "{app}/view" and side at
// "{app}/side", as the View itself, one publication after another. Present returns at once; a publication that fails is written to the run log.
internal sealed class ViewPresenter(DigitalBrainConnection brain, string appKey, long generation) : IPresenter
{
    private readonly Lock _gate = new();
    private readonly ViewPublisher _main = new(brain.Get<IView>(appKey + "/view"), generation);
    private readonly ViewPublisher _side = new(brain.Get<IView>(appKey + "/side"), generation);
    private Task _tail = Task.CompletedTask;

    public void Present(View? side, View? main)
    {
        if (side is not null) { Enqueue(_side, side); }
        if (main is not null) { Enqueue(_main, main); }
    }

    public void Toast(Toast toast) => AppLog.Log("toast: " + toast.Message);

    // Completes once everything presented so far has been published.
    public Task DrainAsync()
    {
        lock (_gate) { return _tail; }
    }

    private void Enqueue(ViewPublisher publisher, View view)
    {
        lock (_gate) { _tail = Publish(_tail, publisher, view); }
    }

    private static async Task Publish(Task previous, ViewPublisher publisher, View view)
    {
        await previous.ConfigureAwait(false);
        try { await publisher.Publish(view).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException) { AppLog.Log("The view could not be published: " + error.Message); }
    }
}
