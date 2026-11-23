using DigitalBrain.Neurons;

namespace IntoChat.OS.UI;

// Test hook: a screen reports its activation so a test can hold the live instance (TestOsAppExtensions.Install).
public static class AppActivationScope
{
    private static readonly AsyncLocal<Action<Neuron>?> Observer = new();

    internal static IDisposable Capture(Action<Neuron> onActivated)
    {
        var previous = Observer.Value;
        Observer.Value = onActivated;
        return new Reset(previous);
    }

    public static void Notify(Neuron neuron) => Observer.Value?.Invoke(neuron);

    private sealed class Reset(Action<Neuron>? previous) : IDisposable
    {
        public void Dispose() => Observer.Value = previous;
    }
}
