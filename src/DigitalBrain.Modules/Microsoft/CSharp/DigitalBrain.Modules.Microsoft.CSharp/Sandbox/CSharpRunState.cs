using DigitalBrain.Microsoft.CSharp.Files;

namespace DigitalBrain.Microsoft.CSharp.Sandbox;

internal sealed record CSharpRunState(CSharpFileStatus Status, int? ExitCode, DateTimeOffset? StartedAt)
{
    public static readonly CSharpRunState Stopped = new(CSharpFileStatus.Stopped, null, null);
}
