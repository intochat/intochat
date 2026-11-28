namespace IntoChat.OS.Modules.Apps.Flows;

[DigitalBrain.Signal("loop.self-improvement.changed")]
public sealed record SelfImprovementLoopChanged(string Status, int RunsToday) : DigitalBrain.Signal;
