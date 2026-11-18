using System.ComponentModel;
using DigitalBrain;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps.AppRuns;

// A synapse of the App kind: an installed sandbox app's behavior. What makes it act is a trigger: a command someone sends, or a tick of
// one of its schedules ([Every("5m")]). Each trigger is one run, keyed by its run id, so it shows on the runs index beside flows,
// workflows and code blocks; the app itself stays loaded in the sandbox container between runs.
public enum AppTrigger { Command, Schedule }

public enum AppRunStatus { Running, Completed, Failed }

// Name is the command, or the schedule's cadence for a tick.
public sealed record AppRunView(string RunId, string App, AppTrigger Trigger, string Name, AppRunStatus Status, bool Handled, string? ResultJson, string? Error);

[Signal("app.run-started")]
public sealed record AppRunStarted(string RunId, string App, AppTrigger Trigger, string Name) : DigitalBrain.Signal;

[Signal("app.run-done")]
public sealed record AppRunDone(string RunId, bool Succeeded, string? Output, string? Error) : DigitalBrain.Signal;

[NeuronAlias("app.run"), DefaultNeuronType("app.run")]
[Description("One run of an installed sandbox app: a command sent to it or a tick of its schedule. Start it, read how it ended.")]
public interface IAppRun
{
    [Description("Run the installed app once: trigger Command sends the command name with an optional JSON payload; Schedule ticks its schedule name. A run starts once; starting again returns it as it is.")]
    Task<AppRunView> Start(AppTrigger trigger, string app, string name, string? payloadJson);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<AppRunView?> Read();
}

// An installed sandbox app as a synapse: what it answers (its commands) and when it acts by itself (its schedules).
public sealed record DeployedApp(string App, string[] Commands, string[] Schedules);

// One deployed sandbox app (key deployed/{app}): set when the app is deployed, cleared when it is removed. It holds one durable
// reminder per schedule, and each due tick starts an app run.
[NeuronAlias("app.deployed"), DefaultNeuronType("app.deployed")]
[Description("One installed sandbox app: its commands and its schedules; each due tick is a run.")]
public interface IDeployedApp
{
    static string KeyOf(string brain, string app) => $"{brain}/deployed/{app}";

    Task Set(string app, string[] commands, string[] schedules);

    Task Clear();

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<DeployedApp?> Read();
}

// Every deployed sandbox app in a brain (key deployed/index), kept by the apps themselves.
[NeuronAlias("app.deployed-index"), DefaultNeuronType("app.deployed-index")]
[Description("The installed sandbox apps of this brain with their commands and schedules.")]
public interface IDeployedApps
{
    static DigitalBrain.NeuronId Key(string brain) => new(new DigitalBrain.BrainId(brain), "app.deployed-index", "deployed/index");

    Task Put(DeployedApp app);

    Task Remove(string app);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<DeployedApp[]> List();
}
