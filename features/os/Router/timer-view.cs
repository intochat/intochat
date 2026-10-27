using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Text.Signals;

// The "Timer" text part is derived state: rebuilt from the timers the router started and the ticks it
// heard whenever either changes, newest timer first.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var started = brain.Get<IText>(Jev.Name(brain, "timers"));
var rang = brain.Get<IText>(Jev.Name(brain, "timer-rang"));
await brain.React<TextChanged>(Sources.Of(started, rang), _ => Timers.Show(brain), brain.Stopping);
