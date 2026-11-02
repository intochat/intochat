/*
# Usage
Show this brain's recorded compute spending over the last 30 days.
Amounts are USD, converted from micro-credits. Never expose private account balances
in the brain's shared presentation.
Read through the scope-checked compute neuron. Refresh rereads authoritative state.
No payments or simulated top-ups are offered by this app.
*/
using IntoChat.OS.Modules.Apps;
static class App
{
    public const string Name = "usage";
    public const string Version = "0.1.3";
    public static readonly AppManifest Manifest = new(
        Title: "Usage", Description: "This brain's compute spending.",
        Operations: [new("open", "Read current usage."), new("refresh", "Refresh recorded usage.")],
        Settings: [], Slots: [], Icon: "icon.svg");
}
