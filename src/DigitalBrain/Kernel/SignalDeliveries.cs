using System.Runtime.CompilerServices;

namespace DigitalBrain;

// A filter over brain.On<T> (an app operation's synapse reading its app's AppRequested) marks the signals it
// passes over, so the runtime client acknowledges a batch nobody handled as skipped, not as a run.
// Internal: scripts never see it; the filters and the client reach it by InternalsVisibleTo.
internal static class SignalDeliveries
{
    private static readonly ConditionalWeakTable<Signal, object> Skipped = [];

    internal static void Skip(Signal signal) => Skipped.AddOrUpdate(signal, Skipped);

    internal static bool WasSkipped(Signal signal) => Skipped.TryGetValue(signal, out _);
}
