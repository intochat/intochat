using IntoChat.OS.Apps.Signals;

namespace IntoChat.OS.Microsoft.CSharp.Runtime;

// A subscription selector names one AppRequested operation. Any other signal, and a subscription
// with no selector, is delivered whole. Matching uses the signal itself, so the runtime does not
// take a dependency on how a script would have skipped it after the fact.
// A run revision that only contains the subscriber is not delivered: waking it would record another
// run and wake it again. A revision that also contains someone else still is.
internal static class SubscriptionFilter
{
    public static bool Accepts(string? selector, Signal signal)
    {
        if (string.IsNullOrEmpty(selector) || signal is not AppRequested requested) { return true; }
        return string.Equals(requested.Operation, selector, StringComparison.Ordinal);
    }

    public static bool Delivers(string behavior, string? selector, Signal signal)
    {
        if (!Accepts(selector, signal)) { return false; }
        return signal is not BrainRunsChanged { Behaviors: { Length: > 0 } behaviors }
            || behaviors.Any(owner => !string.Equals(owner, behavior, StringComparison.Ordinal));
    }
}
