using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
namespace IntoChat.OS.Testing.Tests.Integration;

public sealed class HarnessCancellationFacts
{
    [Trait("host", "own")]
    // Quarantined (timing): https://github.com/intochat/os/issues/315
    [Fact(Explicit = true)]
    public async Task CanceledStartupCancelsAndJoinsTheDeployment()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var service = new HeldStartup();
        var starting = TestBrain.Create()
            .ConfigureServices(services => services.AddSingleton<IHostedService>(service))
            .StartAsync(cancel.Token);
        try
        {
            await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            Assert.True(service.Canceled, "Cancellation must reach startup before the host returns.");
        }
        finally
        {
            service.Release.TrySetResult();
            try { await starting; } catch (OperationCanceledException) { }
        }
    }

    [Trait("host", "own")]
    // Quarantined (timing): https://github.com/intochat/os/issues/315
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbacksCannotBypassTheShutdownDeadline(bool throws)
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = await TestBrain.Create()
            .WithExecution(new() { CleanupTimeout = TimeSpan.FromSeconds(2) })
            .StartAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        // The registration outlives the body. Disposed inside it, the body could unregister the callback before the
        // canceller reached it: when cancellation lands before Task.Delay subscribes, or when the delay's own callback
        // (registered later, so run first) resumes the body inline. The callback then never ran and nothing threw.
        CancellationTokenRegistration registration = default;
        var run = brain.RunBehavior(async (_, token) =>
        {
            registration = token.Register(() =>
            {
                if (throws) { throw new InvalidOperationException("callback failed"); }
                release.Wait();
            });
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Task? disposing = null;
        try
        {
            disposing = brain.DisposeAsync().AsTask();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposing.WaitAsync(TimeSpan.FromSeconds(7), ct));
            if (throws) { Assert.Contains("callback failed", failure.ToString()); }
            else { Assert.Contains("timed out", failure.ToString(), StringComparison.OrdinalIgnoreCase); }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => brain.SubscribeAsync<Number>(brain.Get<ITestEmitter>("closed"), ct));
        }
        finally
        {
            release.Set();
            if (disposing is not null) { try { await disposing; } catch (Exception) { } }
            try { await run.Completion; } catch (OperationCanceledException) { }
            await registration.DisposeAsync();
        }
    }

    private sealed class HeldStartup : IHostedService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
