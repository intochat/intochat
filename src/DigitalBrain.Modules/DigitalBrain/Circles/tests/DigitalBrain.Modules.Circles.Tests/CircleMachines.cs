namespace DigitalBrain.Circles.Tests;

// Members share one runtime; their permissions remain separate.
internal sealed class CircleMachines : IAsyncDisposable
{
    private ITestBrain? runtime;
    public static Task<CircleMachines> Start(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new CircleMachines());
    }
    public async Task<ITestBrain> Machine(CancellationToken ct, Func<TestBrain, TestBrain>? configure = null)
        => runtime ??= await CircleBrainRooms.Start(ct, configure);
    public async ValueTask DisposeAsync() { if (runtime is not null) { await runtime.DisposeAsync(); } }
}
