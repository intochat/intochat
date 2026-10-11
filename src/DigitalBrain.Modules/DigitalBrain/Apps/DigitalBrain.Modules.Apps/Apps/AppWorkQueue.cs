using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Apps;

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
        try { await work(scope.ServiceProvider, CancellationToken.None); }
        catch (Exception error) { log.LogError(error, "App background job {Job} failed", job); }
    }
}
