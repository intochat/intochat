using System.Reflection;
using System.Runtime.CompilerServices;

namespace DigitalBrain.Authority.Neurons;

// brain.On<T> for a T known only by name at runtime.
internal static class SignalWatch
{
    private static readonly MethodInfo Opener = typeof(SignalWatch).GetMethod(nameof(Typed), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IAsyncEnumerable<Signal> Open(IDigitalBrain brain, INeuron source, Type signal, Func<Task> onSubscribed, CancellationToken ct)
        => (IAsyncEnumerable<Signal>)Opener.MakeGenericMethod(signal).Invoke(null, [brain, source, onSubscribed, ct])!;

    private static async IAsyncEnumerable<Signal> Typed<T>(IDigitalBrain brain, INeuron source, Func<Task> onSubscribed, [EnumeratorCancellation] CancellationToken ct) where T : Signal
    {
        await foreach (var signal in brain.On<T>(source, onSubscribed, ct).WithCancellation(ct)) { yield return signal; }
    }
}
