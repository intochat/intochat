using DigitalBrain.Client;
using DigitalBrain.Time.Timers.Signals;
using ITimer = DigitalBrain.Time.Timers.ITimer;

// A behavior: the brain compiles it and loads it into its sandbox session; settings arrive with the assignment.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var timer = brain.Get<ITimer>(brain.Setting("TimerId") ?? "tea");
var stopAfterFirstTick = brain.Setting("Smoke") == "true";
Console.WriteLine("TimerReport ready");
await foreach (var tick in brain.On<TimerTick>(timer, brain.Stopping))
{
    Console.WriteLine($"TimerTick {tick.TimerId} {tick.ObservedAt:O}");
    if (stopAfterFirstTick) { break; }
}
