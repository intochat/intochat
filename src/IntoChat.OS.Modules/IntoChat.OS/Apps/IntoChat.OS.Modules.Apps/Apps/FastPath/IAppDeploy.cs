using IntoChat.OS.Framework;
using Orleans.Concurrency;

namespace IntoChat.OS.Apps;

// The fast-path writer's side of an app, plumbing no edge can address (not an INeuron contract): the writer
// deploys outside the app's turn, so the app hands it the program to run and the principal it runs as.
[Alias("apps.deploy")]
internal interface IAppDeploy : IGrainWithStringKey
{
    // Queue after activation and serialize with edits; INeuron.Watch already interleaves runtime callbacks.
    [OneWay] Task Restore();
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
    [property: Id(2)] CallerContext? Principal);
