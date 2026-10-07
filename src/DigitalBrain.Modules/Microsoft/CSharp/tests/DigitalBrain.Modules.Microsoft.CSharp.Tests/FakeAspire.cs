using DigitalBrain.Framework;
using DigitalBrain.Microsoft.Aspire;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Sandbox;
using Orleans.Runtime;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

[Alias("test.fake-aspire-probe")]
public interface IFakeAspireProbe : INeuron
{
    Task<int> Starts();
    Task FailStarts();
}

// The AppHost as the runner sees it: the sandbox starts stopped and reports Running once asked to start.
[GrainType("microsoft.aspire")]
public sealed class FakeAspire : Neuron, IAspire, IFakeAspireProbe
{
    private AspireResource _sandbox = new(CSharpSandbox.ResourceName, "Container", "NotStarted", null, []);

    private int _starts;
    private bool _failStarts;

    public Task<int> Starts() => Task.FromResult(_starts);

    public Task FailStarts() { _failStarts = true; return Task.CompletedTask; }

    public Task<IReadOnlyList<AspireResource>> ListResources(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AspireResource>>([_sandbox]);

    public async Task StartResource(string name, CancellationToken cancellationToken = default)
    {
        _starts++;
        _sandbox = _failStarts
            ? _sandbox with { State = "FailedToStart", Health = null, Urls = [] }
            : _sandbox with { State = "Running", Health = "Healthy", Urls = [FakeSandbox.Url] };
        await PublishAsync(new ResourceStateChanged(name, _sandbox.State, _sandbox.Health, "NotStarted"));
    }

    public Task StopResource(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RestartResource(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
