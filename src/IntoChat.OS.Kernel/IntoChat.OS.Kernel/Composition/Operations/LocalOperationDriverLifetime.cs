using System.ComponentModel;
using System.Security;
using System.Text.Json;
using DigitalBrain.Operations;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Framework.Machine;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Kernel.Composition.Operations;

// Local process evidence is independent of Aspire's allocated Orleans ports. The installation identity must match
// before probing a PID; a PID namespace we cannot identify contributes no evidence to recovery.
internal sealed class LocalOperationDriverLifetime(IMachineIdentity? identity, ILocalProcessProbe processes,
    ILogger<LocalOperationDriverLifetime>? logger = null) : IOperationDriverLifetime
{
    private readonly ILogger<LocalOperationDriverLifetime> _logger = logger ?? OsConsoleLog.For<LocalOperationDriverLifetime>();

    public string? Capture()
    {
        try
        {
            var machine = identity?.Id;
            var space = processes.Namespace;
            if (string.IsNullOrWhiteSpace(machine) || string.IsNullOrWhiteSpace(space)) { return null; }
            var pid = processes.CurrentProcessId;
            if (pid <= 0) { return null; }
            var process = processes.Observe(pid);
            return process is { Alive: true, StartedUtcTicks: > 0 } && process.StartedUtcTicks <= DateTime.MaxValue.Ticks
                ? JsonSerializer.Serialize(new Token(1, machine, space, pid, process.StartedUtcTicks)) : null;
        }
        catch (Exception error) when (Unavailable(error))
        {
            _logger.LogDebug("Local operation lifetime capture is unavailable ({ExceptionType}).", error.GetType().Name);
            return null;
        }
    }

    public bool? HasEnded(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) { return null; }
        try
        {
            var saved = JsonSerializer.Deserialize<Token>(token);
            if (saved is not { Version: 1, ProcessId: > 0, StartedUtcTicks: > 0 }
                || saved.StartedUtcTicks > DateTime.MaxValue.Ticks || string.IsNullOrWhiteSpace(saved.Machine)
                || string.IsNullOrWhiteSpace(saved.Namespace) || saved.Machine != identity?.Id || saved.Namespace != processes.Namespace)
            { return null; }
            var process = processes.Observe(saved.ProcessId);
            return process.Alive switch
            {
                false => true,
                true when process.StartedUtcTicks > 0 && process.StartedUtcTicks <= DateTime.MaxValue.Ticks
                    => process.StartedUtcTicks != saved.StartedUtcTicks,
                _ => null,
            };
        }
        catch (JsonException) { return null; }
        catch (Exception error) when (Unavailable(error))
        {
            _logger.LogDebug("Local operation lifetime observation is unavailable ({ExceptionType}).", error.GetType().Name);
            return null;
        }
    }

    private static bool Unavailable(Exception error)
        => error is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or InvalidOperationException or NotSupportedException;

    private sealed record Token(int Version, string Machine, string Namespace, int ProcessId, long StartedUtcTicks);
}

internal interface ILocalProcessProbe
{
    string? Namespace { get; }
    int CurrentProcessId { get; }
    ProcessObservation Observe(int processId);
}

internal readonly record struct ProcessObservation(bool? Alive, long StartedUtcTicks = 0);
