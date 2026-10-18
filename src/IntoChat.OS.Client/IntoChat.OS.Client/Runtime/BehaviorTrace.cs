namespace IntoChat.OS.Client.Runtime;

// The trace a behavior is currently reacting to, shared by its connections, proxies and subscriptions.
// A behavior handling two subscriptions at once joins the latest arrival's trace.
internal sealed class BehaviorTrace
{
    public string? TraceParent { get; set; }
}
