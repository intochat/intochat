using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Contracts.Testing;

namespace IntoChat.OS.Modules.Apps.Testing;

public sealed class TestVerificationService : ITestVerificationService
{
    private readonly Func<string, ITestVerificationNeuron>? _neuronResolver;
    private readonly IServiceProvider? _services;

    public TestVerificationService(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public TestVerificationService(Func<string, ITestVerificationNeuron> neuronResolver)
    {
        ArgumentNullException.ThrowIfNull(neuronResolver);
        _neuronResolver = neuronResolver;
    }

    public TestVerificationService(ITestVerificationNeuron neuron)
    {
        ArgumentNullException.ThrowIfNull(neuron);
        _neuronResolver = _ => neuron;
    }

    private ITestVerificationNeuron GetNeuron(string suiteId)
    {
        if (_neuronResolver is not null)
        {
            return _neuronResolver(suiteId);
        }

        if (_services is not null)
        {
            return SiloNeurons.Get<ITestVerificationNeuron>(_services, new NeuronId(new BrainId("local"), "testing.verification", suiteId));
        }

        throw new InvalidOperationException("No test verification neuron resolver configured.");
    }

    public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var neuron = GetNeuron(request.SuiteName);
        return neuron.RunTargetedSuiteAsync(request);
    }

    public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testSuiteId);
        var neuron = GetNeuron(testSuiteId);
        return neuron.GetHistoricalRunsAsync(testSuiteId);
    }
}
