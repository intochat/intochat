using System.Reflection;
using System.Runtime.CompilerServices;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppInvocationReadinessFacts
{
    [Fact]
    public async Task CancellationDuringRegistrationSettlesThePendingRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("invocation-readiness-facts", "cancel"));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gated = new GatedBrain(brain);
        await using var stream = app.Invocations(gated, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        var first = stream.MoveNextAsync().AsTask();
        await gated.Started.Task.WaitAsync(ct);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(gated.Registered);
    }

    [Fact]
    public async Task RegistrationFailureReachesTheConsumerWithoutWaitingForReadiness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("invocation-readiness-facts", "failure"));
        var gated = new GatedBrain(brain);
        await using var stream = app.Invocations(gated, ct).GetAsyncEnumerator(ct);
        var first = stream.MoveNextAsync().AsTask();
        await gated.Started.Task.WaitAsync(ct);
        var failure = new InvalidOperationException("Registration failed");
        gated.Release.TrySetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
    }

    [Fact]
    public async Task AnInvocationDuringRegistrationIsRecoveredWithoutAnotherSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
        var app = brain.Get<IApp>(IApp.Key("invocation-readiness-facts", "gap"));
        await app.Create(new AppManifest("Echo", "", [new("open", "")], [], []), "spec");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gated = new GatedBrain(brain);
        var observed = DispatchProxy.Create<IApp, PendingOrderProbe>();
        var probe = (PendingOrderProbe)(object)observed;
        probe.Inner = app;
        probe.Ready = () => gated.Registered;
        await using var stream = observed.Invocations(gated, ["open"], lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        var first = stream.MoveNextAsync().AsTask();
        try
        {
            await gated.Started.Task.WaitAsync(ct);
            var invocation = await app.Invoke(new(Guid.NewGuid(), "open", "during registration"));
            gated.Release.TrySetResult();
            Assert.False(probe.ReadBeforeReady, "Recovery read happened before subscription registration.");
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5), ct));
            Assert.Equal(invocation.Id, stream.Current.InvocationId);
            Assert.Equal("during registration", stream.Current.Input);
        }
        finally
        {
            await lifetime.CancelAsync();
            gated.Release.TrySetResult();
            try { await first; } catch (Exception) { }
        }
    }

    // The app remains a real grain. Record read ordering without changing its result.
    public class PendingOrderProbe : DispatchProxy
    {
        public IApp Inner { get; set; } = null!;
        public Func<bool> Ready { get; set; } = null!;
        public bool ReadBeforeReady { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IApp.Pending) && !Ready())
            { ReadBeforeReady = true; }
            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class GatedBrain(ModuleBrain inner) : IDigitalBrain
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Registered { get; private set; }
        public IKernel Self => inner.Self;
        public T Get<T>(NeuronId id) where T : INeuron => ((IDigitalBrain)inner).Get<T>(id);
        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => On<T>(source, static () => Task.CompletedTask, cancellationToken);
        public async IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed,
            [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : Signal
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await using var subscription = await inner.SubscribeAsync<T>(source is PendingOrderProbe probe ? probe.Inner : source, cancellationToken);
            Registered = true;
            await onSubscribed();
            await foreach (var signal in subscription.ReadAllAsync(cancellationToken)) { yield return signal; }
        }
    }
}
