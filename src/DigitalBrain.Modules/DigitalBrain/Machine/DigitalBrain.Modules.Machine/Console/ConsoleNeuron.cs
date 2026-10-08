using System.Diagnostics;
using DigitalBrain.Framework;
using Microsoft.Extensions.Options;

namespace DigitalBrain.Machine;

[GrainType("machine.console")]
internal sealed class ConsoleNeuron(IOptions<MachineOptions> options) : Neuron, IConsole
{
    public async Task<CommandResult> Run(string command, string? workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) { throw new InvalidOperationException("The machine module is disabled. Set DigitalBrain:Modules:machine:Options:Enabled=true."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var seconds = timeoutSeconds is < 1 or > 600 ? 60 : timeoutSeconds;
        var (shell, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c " + command)
            : ("/bin/sh", "-c " + command);
        var info = new ProcessStartInfo(shell, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) { info.WorkingDirectory = workingDirectory; }
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {shell}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception error) when (error is InvalidOperationException or NotSupportedException) { }
            throw;
        }
        return new CommandResult(process.ExitCode, Clip(await stdout), Clip(await stderr));
    }

    private string Clip(string text)
        => text.Length <= options.Value.MaxOutputChars ? text : text[..options.Value.MaxOutputChars];
}
