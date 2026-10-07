namespace DigitalBrain.Microsoft.CSharp.Sandbox;

// The brain's one copy of the sandbox host's HTTP shapes. The image keeps its own, checked by SandboxWireFacts.
internal sealed record SandboxRunRequest(string Source, Dictionary<string, string> Environment);

internal sealed record SandboxRunStatus(string Identifier, string Status, int? ExitCode, DateTimeOffset StartedAt);

internal static class SandboxRunStates
{
    public const string Running = "Running";
    public const string Exited = "Exited";
}
