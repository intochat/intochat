namespace DigitalBrain.Framework.Contracts;

// A signal on the program surface: its module and its shape, "Full.Name { Type Property; ... }".
public sealed record SignalType(string ModuleId, string Shape);
