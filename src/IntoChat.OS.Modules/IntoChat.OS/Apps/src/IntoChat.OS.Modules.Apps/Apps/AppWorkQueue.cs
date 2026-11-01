using System;
using System.Threading;
using IntoChat.OS.Modules.Apps.Contracts.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

// Runs builder jobs off the request path. The process is a cache: every job's
// durable state (Building status, activity) lives on the app neuron, so a lost
// job costs one re-trigger, never data.
internal sealed class AppWorkQueue(IServiceScopeFactory scopes, ILogger<AppWorkQueue> log)
{
    public void Enqueue(string job, Func<IServiceProvider, CancellationToken, Task> work)
        => _ = Run(job, work);

    private async Task Run(string job, Func<IServiceProvider, CancellationToken, Task> work)
    {
        // Off the caller's scheduler: a job an app neuron enqueues must not run its model calls on the grain's.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        using var scope = scopes.CreateScope();
        var taskEngine = scope.ServiceProvider.GetService<ITaskEngine>();
        AgentTask? task = null;

        if (taskEngine is not null)
        {
            try
            {
                task = await taskEngine.CreateTaskAsync(new CreateTaskRequest(
                    job,
                    $"Background execution of job {job}",
                    "AppWorkQueue",
                    [new TaskStepDefinition(job, $"Execute {job}", "AppWorkQueue")]));
                await taskEngine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Running);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Could not initialize task engine for job {Job}", job);
            }
        }

        try
        {
            await work(scope.ServiceProvider, CancellationToken.None);
            if (taskEngine is not null && task is not null)
            {
                await taskEngine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Completed, "Success");
            }
        }
        catch (Exception error)
        {
            log.LogError(error, "App background job {Job} failed", job);
            if (taskEngine is not null && task is not null)
            {
                try
                {
                    await taskEngine.AdvanceTaskStepAsync(task.TaskId, 0, TaskStepStatus.Failed, error.Message);
                    await taskEngine.RollbackTaskAsync(task.TaskId, 0, error.Message);
                }
                catch (Exception rollbackError)
                {
                    log.LogDebug(rollbackError, "Failed to record task rollback for job {Job}", job);
                }
            }
        }
    }
}
