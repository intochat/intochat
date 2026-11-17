using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.AppRuns;

// A synapse of the App kind: an installed sandbox app's behavior, expressed as its commands. Each command sent to the app is one run,
// keyed by its run id, so it shows on the runs index beside flows, workflows and code blocks; the app itself stays loaded in the
// sandbox container between commands.
public enum AppCommandStatus { Running, Completed, Failed }

public sealed record AppCommandRunView(string RunId, string App, string Command, AppCommandStatus Status, bool Handled, string? ResultJson, string? Error);

[Signal("app.command-started")]
public sealed record AppCommandStarted(string RunId, string App, string Command) : DigitalBrain.Signal;

[Signal("app.command-done")]
public sealed record AppCommandDone(string RunId, bool Succeeded, string? Output, string? Error) : DigitalBrain.Signal;

[NeuronAlias("app.command"), DefaultNeuronType("app.command")]
[Description("One command sent to an installed sandbox app: send it with a JSON payload, read how it ended.")]
public interface IAppCommandRun
{
    [Description("Send command to the installed app with an optional JSON payload. A run sends once; starting again returns it as it is.")]
    Task<AppCommandRunView> Start(string app, string command, string? payloadJson);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<AppCommandRunView?> Read();
}
