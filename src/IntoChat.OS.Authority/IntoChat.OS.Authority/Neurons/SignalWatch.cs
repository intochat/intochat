using System.Reflection;

namespace IntoChat.OS.Authority.Neurons;

// brain.On<T> for a T known only by name at runtime. A client-session pass-through (06-06c): the typed stream is handed
// out as is (IAsyncEnumerable is covariant), so no loop re-yields it; the caller's session stream is the only reader.
internal static class SignalWatch
{
    private static readonly MethodInfo Opener = typeof(SignalWatch).GetMethod(nameof(Typed), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static IAsyncEnumerable<Signal> Open(IIntoChatOS brain, INeuron source, Type signal, Func<Task> onSubscribed, CancellationToken ct)
        => (IAsyncEnumerable<Signal>)Opener.MakeGenericMethod(signal).Invoke(null, [brain, source, onSubscribed, ct])!;

    private static IAsyncEnumerable<Signal> Typed<T>(IIntoChatOS brain, INeuron source, Func<Task> onSubscribed, CancellationToken ct) where T : Signal
        => brain.On<T>(source, onSubscribed, ct);
}
