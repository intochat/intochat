using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Flutter.Views;

/// <summary>Publishes changed views in order under one program generation; the view neuron skips an unchanged one.</summary>
public sealed class ViewPublisher(IView view, long generation)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? previous;

    public async Task Publish(View next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var serialized = System.Text.Encoding.UTF8.GetString(RuntimeProtocol.SerializeToUtf8Bytes(next, typeof(View)));
        await gate.WaitAsync();
        try
        {
            if (serialized == previous) { return; }
            await view.Publish(generation, next);
            previous = serialized;
        }
        finally { gate.Release(); }
    }
}
