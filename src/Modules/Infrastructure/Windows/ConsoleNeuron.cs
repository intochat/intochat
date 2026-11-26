using System.Diagnostics;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Storage.Confinement;
using DigitalBrain.Log;
using DigitalBrain.Errors;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS;
using DigitalBrain.Identity.Local.Machine;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Microsoft.Windows;

// A plain neuron (06-08) keyed {connectionKey}/console.
[DigitalBrain.Neurons.Neuron("windows.console")]
internal sealed partial class ConsoleNeuron(MachineId thisMachine) : DigitalBrain.Neurons.Neuron, IConsole
{
    [DigitalBrain.Slot(Required = false)]
    private partial DigitalBrain.Components.SlotCollection<IWindows> WindowsMachines { get; }

    private const int MaxOutputChars = 20_000;

    // IConsole.Run's ResponseTimeout is five minutes; the command must end before the call does.
    private const int MaxTimeoutSeconds = 270;

    public async Task<CommandResult> Run(string command, string? workingDirectory = null, int timeoutSeconds = 60, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var connection = WindowsKeys.Connection(Id.FullKey());
        if (ConnectionKeys.Parse(connection).ConnectionId != thisMachine.Value)
        { throw new ServiceUnavailableException("This Windows machine is not this silo's machine."); }
        var roots = await WindowsMachines[DigitalBrain.Contracts.PlainNeuronContracts.IdOf(typeof(IWindows), connection)].Roots();
        var directory = PathConfinement.Resolve(roots, string.IsNullOrWhiteSpace(workingDirectory) ? (roots.Length > 0 ? roots[0] : ".") : workingDirectory);
        var seconds = Math.Clamp(timeoutSeconds, 1, MaxTimeoutSeconds);

        // cmd.exe parses its own command line, so the command goes through verbatim rather than argv-quoted.
        var start = new ProcessStartInfo("cmd.exe", "/c " + command)
        { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        ChildEnvironmentPolicy.Scrub(start);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested) { throw; }
            throw new TimeoutException($"The command ran longer than {seconds} seconds.");
        }
        return new CommandResult(process.ExitCode, Clip(await output), Clip(await error));
    }

    // The process may have exited between the timeout and the kill.
    private static bool TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); return true; }
        catch (Exception kill) when (kill is InvalidOperationException or NotSupportedException)
        {
            OsConsoleLog.For<ConsoleNeuron>().LogSwallowed(kill, "terminating exited cmd.exe process");
            return false;
        }
    }

    private static string Clip(string text) => text.Length <= MaxOutputChars ? text : text[..MaxOutputChars];
}
