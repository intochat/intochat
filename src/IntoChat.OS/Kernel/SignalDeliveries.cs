using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace IntoChat.OS;

// A filter over brain.On<T> (an app operation's synapse reading its app's AppRequested) marks the signals it
// passes over, so the runtime client acknowledges a batch nobody handled as skipped, not as a run.
// Runtime SPI: the Apps.Contracts filters call Skip and the runtime clients read WasSkipped. Hidden from
// script completion; it only marks local delivery bookkeeping. Transitional (Phase 4/6).
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SignalDeliveries
{
    private static readonly ConditionalWeakTable<Signal, object> Skipped = [];

    public static void Skip(Signal signal) => Skipped.AddOrUpdate(signal, Skipped);

    public static bool WasSkipped(Signal signal) => Skipped.TryGetValue(signal, out _);
}
