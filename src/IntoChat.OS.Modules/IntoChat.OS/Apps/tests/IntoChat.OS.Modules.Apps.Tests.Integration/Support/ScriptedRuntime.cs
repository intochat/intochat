using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

[DigitalBrain.Contracts.NeuronAlias("test.apps.scripted-runtime"), DigitalBrain.Contracts.DefaultNeuronType("microsoft.csharp.runtime")]
public interface IScriptedRuntime
{
    Task Script(BehaviorView[] behaviors);
}

// Stands where the brain runtime would, which this host has no csharp module for: Inspect answers with the
// behaviors a fact scripts, and with none until one does. Each test brain is its own silo, so nothing carries over.
// A plain neuron (06-08) of the runtime's type, hosted by the test assembly's neuron module.
[DigitalBrain.Neuron("microsoft.csharp.runtime")]
public sealed class ScriptedRuntime : DigitalBrain.Neuron, IBrainRuntime, IScriptedRuntime
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
