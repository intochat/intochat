using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp.Runtime;
using Orleans.Metadata;

namespace DigitalBrain.Modules.Apps.Tests.Integration;

[Alias("test.apps.scripted-runtime"), DefaultGrainType("test.apps.scripted-runtime")]
public interface IScriptedRuntime : INeuron
{
    Task Script(BehaviorView[] behaviors);
}

// Stands where the brain runtime would, which this host has no csharp module for: Inspect answers with the
// behaviors a fact scripts, and with none until one does. Each test brain is its own silo, so nothing carries over.
[GrainType("microsoft.csharp.runtime")]
public sealed class ScriptedRuntime : Neuron, IBrainRuntime, IScriptedRuntime
{
    private BehaviorView[] _behaviors = [];

    public Task Script(BehaviorView[] behaviors)
    {
        _behaviors = behaviors;
        return Task.CompletedTask;
    }

    public Task<BrainRuntimeSnapshot> Inspect(CancellationToken cancellationToken = default)
        => Task.FromResult(new BrainRuntimeSnapshot(SessionStatus.Connected, "session", null, null, _behaviors));

    public Task Touch(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
