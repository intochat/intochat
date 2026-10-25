using IntoChat.OS;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk.Modules;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// Composes the test's own contracts, so behaviors may call them as they call an installed module's.
public sealed class PingerModule() : Module(new ModuleId("tests-pinger"))
{
    public override System.Reflection.Assembly? Contracts => typeof(IPinger).Assembly;
    public override void Configure(ISiloBuilder silo) { }
}

[Alias("tests.csharp.pinger")]
[Public]
public interface IPinger : INeuron
{
    Task Ping(int number);

    Task Request(string operation);

    Task<int> Double(int number, CancellationToken cancellationToken = default);

    Task<string> Caller();
    Task RefuseUnavailable();
    Task RefuseStale();

    // The caller the first Unwatch carried, as Caller() reports one, once one came: unwatches run off the runtime's turn.
    [global::Orleans.Concurrency.AlwaysInterleave] Task<string> Unwatched();
}

[GenerateSerializer, Alias("tests.csharp.pinged")]
public sealed record Pinged([property: Id(0)] int Number) : Signal;

[GenerateSerializer, Alias("tests.csharp.ignored")]
public sealed record Ignored([property: Id(0)] int Number) : Signal;

// A trigger source that publishes the signal an armed file waits for and one it must ignore, and a
// callable contract that reports the caller context the script edge stamped.
public sealed class Pinger : Neuron, IPinger
{
    public Task RefuseStale() => throw new RevisionConflictException("Expected revision 3; current revision is 7.", 7);
    public Task RefuseUnavailable() => throw new ServiceUnavailableException("Sorry, the service is not available at the moment.");
    public async Task Ping(int number)
    {
        await PublishAsync(new Ignored(number));
        await PublishAsync(new Pinged(number));
    }

    public Task Request(string operation) => PublishAsync(new AppRequested(operation, "", Guid.NewGuid()));

    public Task<int> Double(int number, CancellationToken cancellationToken = default) => Task.FromResult(number * 2);

    private readonly TaskCompletionSource<string> _unwatched = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Caller() => Task.FromResult(Describe());

    public override Task Unwatch(INeuronObserver observer)
    {
        _unwatched.TrySetResult(Describe());
        return base.Unwatch(observer);
    }

    public Task<string> Unwatched() => _unwatched.Task;

    private static string Describe() => CallerContextStamper.TryGet(out var caller)
        ? $"{caller.PrincipalId} {caller.Kind} {caller.StampedBy} {caller.AppId} for {caller.OnBehalfOf}"
        : "unstamped";
}
