using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Contracts.Tasks;
using TaskStatus = IntoChat.OS.Modules.Apps.Contracts.Tasks.TaskStatus;

namespace IntoChat.OS.Modules.Apps.Tasks;

public sealed class TaskEngine : ITaskEngine
{
    private readonly Func<string, ITaskNeuron>? _taskResolver;
    private readonly Func<ITaskDirectoryNeuron>? _directoryResolver;
    private readonly IServiceProvider? _services;

    public TaskEngine(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public TaskEngine(Func<string, ITaskNeuron> taskResolver, Func<ITaskDirectoryNeuron>? directoryResolver = null)
    {
        ArgumentNullException.ThrowIfNull(taskResolver);
        _taskResolver = taskResolver;
        _directoryResolver = directoryResolver;
    }

    private ITaskNeuron GetTaskNeuron(string taskId)
    {
        if (_taskResolver is not null)
        {
            return _taskResolver(taskId);
        }

        if (_services is not null)
        {
            return SiloNeurons.Get<ITaskNeuron>(_services, new NeuronId(new BrainId("local"), "tasks.task", taskId));
        }

        throw new InvalidOperationException("No task neuron resolver configured.");
    }

    private ITaskDirectoryNeuron GetDirectoryNeuron()
    {
        if (_directoryResolver is not null)
        {
            return _directoryResolver();
        }

        if (_services is not null)
        {
            return SiloNeurons.Get<ITaskDirectoryNeuron>(_services, new NeuronId(new BrainId("local"), "tasks.directory", "default"));
        }

        throw new InvalidOperationException("No task directory neuron resolver configured.");
    }

    public async Task<AgentTask> CreateTaskAsync(CreateTaskRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taskId = $"task-{Guid.NewGuid():N}"[..18];
        var neuron = GetTaskNeuron(taskId);
        return await neuron.InitializeAsync(request);
    }

    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var neuron = GetTaskNeuron(taskId);
        return await neuron.GetAsync();
    }

    public async Task<IReadOnlyList<AgentTask>> ListTasksAsync(string? owner = null, TaskStatus? status = null, CancellationToken ct = default)
    {
        var dir = GetDirectoryNeuron();
        return await dir.ListTasksAsync(owner, status);
    }

    public async Task<AgentTask> AdvanceTaskStepAsync(string taskId, int stepIndex, TaskStepStatus newStatus, string? output = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var neuron = GetTaskNeuron(taskId);
        return await neuron.AdvanceStepAsync(stepIndex, newStatus, output);
    }

    public async Task<AgentTask> RollbackTaskAsync(string taskId, int toStepIndex, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var neuron = GetTaskNeuron(taskId);
        return await neuron.RollbackAsync(toStepIndex, reason);
    }

    public async Task<AgentTask> CancelTaskAsync(string taskId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var neuron = GetTaskNeuron(taskId);
        return await neuron.CancelAsync(reason);
    }

    public async Task<TaskVerificationResult> VerifyTaskStepAsync(string taskId, int stepIndex, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var neuron = GetTaskNeuron(taskId);
        return await neuron.VerifyStepAsync(stepIndex);
    }
}
