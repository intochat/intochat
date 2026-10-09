using System.ComponentModel;
using Orleans.Concurrency;

namespace DigitalBrain.Machine;

// Runs a shell command on the host machine (the person's PC). The command is one shell line; on
// Windows it runs through cmd.exe, elsewhere through /bin/sh. This is the edge to the machine.
[Routable]
[Alias("machine.console"), Orleans.Metadata.DefaultGrainType("machine.console")]
public interface IConsole : INeuron
{
    [Description("Run a shell command on the host machine and return its exit code, standard output and standard error. command is the full shell line (for example \"dir\" or \"git status\").")]
    [AlwaysInterleave, ResponseTimeout("00:05:00")]
    Task<CommandResult> Run(string command, string? workingDirectory = null, int timeoutSeconds = 60, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("machine.command-result")]
public sealed record CommandResult(
    [property: Id(0)] int ExitCode,
    [property: Id(1)] string StandardOutput,
    [property: Id(2)] string StandardError);
