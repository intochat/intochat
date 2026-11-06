using DigitalBrain.Runtime.Orleans.Capabilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;

namespace IntoChat.OS.Modules.AI.Testing;

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

    public BrainId ResolveBrainId()
    {
        if (RuntimeCallerContext.TryGet(out var stamped) && stamped?.BrainId is { Length: > 0 } stampedBrain)
        {
            return new BrainId(stampedBrain);
        }

        return new BrainId("local");
    }

    private ITestVerificationNeuron GetNeuron(string suiteId)
    {
        if (_neuronResolver is not null)
        {
            return _neuronResolver(suiteId);
        }

        if (_services is not null)
        {
            return SiloNeurons.Get<ITestVerificationNeuron>(_services, new NeuronId(ResolveBrainId(), "testing.verification", suiteId));
        }

        throw new InvalidOperationException("No test verification neuron resolver configured.");
    }

    public Task<TestExecutionResult> RunTargetedSuiteAsync(TestExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var neuron = GetNeuron(request.SuiteName);
        return neuron.RunTargetedSuiteAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<TestResultSummary>> GetHistoricalRunsAsync(string testSuiteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testSuiteId);
        if (string.IsNullOrWhiteSpace(testSuiteId))
        {
            throw new ArgumentException("TestSuiteId cannot be empty or whitespace.", nameof(testSuiteId));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var neuron = GetNeuron(testSuiteId);
        return neuron.GetHistoricalRunsAsync(testSuiteId, cancellationToken);
    }
}
