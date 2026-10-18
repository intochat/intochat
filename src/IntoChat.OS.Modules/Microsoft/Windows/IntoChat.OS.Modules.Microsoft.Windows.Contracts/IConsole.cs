using System.ComponentModel;
using Orleans.Concurrency;

namespace IntoChat.OS.Microsoft.Windows;

// cmd.exe on one Windows PC, keyed {connectionKey}/console; the working directory must be inside an allowed root.
[Alias("windows.console"), Orleans.Metadata.DefaultGrainType("windows.console")]
public interface IConsole : INeuron
{
    [Description("Run one cmd.exe command line on this Windows machine and return its exit code, standard output and standard error. workingDirectory defaults to the first allowed root.")]
    [AlwaysInterleave, ResponseTimeout("00:05:00")]
    Task<CommandResult> Run(string command, string? workingDirectory = null, int timeoutSeconds = 60, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Alias("windows.command-result")]
public sealed record CommandResult(
    [property: Id(0)] int ExitCode,
    [property: Id(1)] string StandardOutput,
    [property: Id(2)] string StandardError);
