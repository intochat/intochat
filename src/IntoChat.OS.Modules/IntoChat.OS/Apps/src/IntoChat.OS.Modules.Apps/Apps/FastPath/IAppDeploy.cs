using DigitalBrain.Capabilities;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Signals.Watch;

namespace IntoChat.OS.Modules.Apps;

// The fast-path writer's side of an app, plumbing no edge can address (not an INeuron contract): the writer
// deploys outside the app's turn, so the app hands it the program to run and the principal it runs as.
// A module-internal plain contract of the app neuron (06-08), alias unchanged.
[DigitalBrain.Contracts.NeuronAlias("apps.deploy"), DigitalBrain.Contracts.DefaultNeuronType(IntoChat.OS.Connections.AppGrainType.Name)]
internal interface IAppDeploy
{
    // Queued after activation as its own turn, so it serializes with edits.
    [DigitalBrain.OneWayCall] Task Restore();
    // Records the requested synapse's source on its request (never in the app's files) and answers the app's
    // program with it. Throws when the request is not waiting or the program would break the shape.
    Task<StagedProgram> Stage(Guid operationId, string path, string source);
    // What the app runs now: its files alone, as the runtime should hold them.
    Task<StagedProgram> Program();
    // The deploy and putting the runtime back both went unanswered: the request is Unsettled with the reason,
    // and keeps its staged source.
    Task<AppSnapshot> Unsettle(Guid operationId, string reason, long elapsedMs, int attempts, string? baseSynapse);
}

[GenerateSerializer, Alias("apps.staged-program")]
internal sealed record StagedProgram(
    [property: Id(0)] Dictionary<string, string> Sources,
    [property: Id(1)] Dictionary<string, string> Configuration,
    [property: Id(2)] CapabilityCaller? Principal);
