using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.Text.Signals;
using IntoChat.OS.Modules.Time.Timers.Signals;
using ITimer = IntoChat.OS.Modules.Time.Timers.ITimer;

// A tick from a timer the router started is recorded in the timer-rang neuron and the "Timer" text part
// is rebuilt (Timers decides which timer a tick belongs to). The subscription follows the keys in the
// timers neuron, inside this brain only. A tick delivered twice is recorded once.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var scope = Jev.Scope(brain);
var rang = brain.Get<IText>(Jev.Name(brain, "timer-rang"));
var timers = Sources.Named<IText, TextChanged, ITimer>(brain.Get<IText>(Jev.Name(brain, "timers")), async (registry, ct) =>
    [.. Timers.Keys((await registry.Read()).Markdown, scope).Select(key => brain.Get<ITimer>(key))]);
await foreach (var tick in brain.On<TimerTick>(timers, brain.Stopping))
{
    var lines = Timers.Lines((await rang.Read()).Markdown);
    var line = Timers.Line(new Timers.Rang(tick.TimerId, tick.ObservedAt));
    if (!lines.Contains(line)) { await rang.Set(string.Join('\n', lines.TakeLast(Timers.Kept * 2 - 1).Append(line))); }
    await Timers.Show(brain);
}
