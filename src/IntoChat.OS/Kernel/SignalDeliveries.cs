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
    // An os Signal or a plain [Signal] record (06-08: AppRequested is plain).
    private static readonly ConditionalWeakTable<object, object> Skipped = [];

    public static void Skip(object signal) => Skipped.AddOrUpdate(signal, Skipped);

    public static bool WasSkipped(object signal) => Skipped.TryGetValue(signal, out _);
}
