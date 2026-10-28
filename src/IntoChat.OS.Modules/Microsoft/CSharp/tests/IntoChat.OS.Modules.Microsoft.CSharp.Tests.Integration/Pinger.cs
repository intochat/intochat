using IntoChat.OS.Connections.Contracts;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using DigitalBrain.Access;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// Composes the test's own contracts, so behaviors may call them as they call an installed module's; the pinger is a plain neuron
// (06-08), hosted by the test's DigitalBrain module.
[DigitalBrain.Module("tests-pinger")]
public sealed class PingerModule : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new([], [], [], [], []);
    public static System.Reflection.Assembly? Contracts => typeof(IPinger).Assembly;
    public static Type Neurons => typeof(PingerNeurons);
}

[DigitalBrain.Module("tests-pinger")]
public sealed class PingerNeurons : DigitalBrain.Module
{
    public static DigitalBrain.ModuleManifest Manifest { get; } = new(Provides: [], Requires: [], Needs: [], Settings: [], Secrets: []);
}

[DigitalBrain.Contracts.NeuronAlias("tests.csharp.pinger"), DigitalBrain.Contracts.DefaultNeuronType(Pinger.Type)]
[Public]
public interface IPinger
{
    Task Ping(int number);

    Task<int> Double(int number, CancellationToken cancellationToken = default);

    Task<string> Caller();
    Task RefuseUnavailable();
    Task RefuseStale();

    // The caller the first subscription admission carried, as Caller() reports one, once one came: subscriptions are made off the
    // runtime's turn.
    [DigitalBrain.Interleaves] Task<string> Admitted();
}

[DigitalBrain.Signal(Pings.Pinged)]
public sealed record Pinged(int Number) : DigitalBrain.Signal;

[DigitalBrain.Signal(Pings.Ignored)]
public sealed record Ignored(int Number) : DigitalBrain.Signal;

// The aliases a subscription to the pinger names (06-08: a plain source's signals are named by their [Signal] alias).
public static class Pings
{
    public const string Pinged = "tests.csharp.pinged";
    public const string Ignored = "tests.csharp.ignored";
}

// A trigger source that publishes the signal an armed file waits for and one it must ignore, and a
// callable contract that reports the caller context the script edge stamped.
[DigitalBrain.Neuron(Type)]
public sealed class Pinger : DigitalBrain.Neuron, IPinger
{
    public const string Type = "tests.csharp.pinger";

    public Task RefuseStale() => throw new RevisionConflictException("Expected revision 3; current revision is 7.", 7);
    public Task RefuseUnavailable() => throw new ServiceUnavailableException("Sorry, the service is not available at the moment.");
    public async Task Ping(int number)
    {
        await Publish(new Ignored(number));
        await Publish(new Pinged(number));
    }

    public Task<int> Double(int number, CancellationToken cancellationToken = default) => Task.FromResult(number * 2);

    private readonly TaskCompletionSource<string> _admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Caller() => Task.FromResult(Describe());

    protected override Task AdmitSynapse(DigitalBrain.SynapseRequest request)
    {
        _admitted.TrySetResult(Describe());
        return base.AdmitSynapse(request);
    }

    public Task<string> Admitted() => _admitted.Task;

    private static string Describe() => CallerContextStamper.TryGet(out var caller)
        ? $"{caller.PrincipalId} {caller.Kind} {caller.StampedBy} {caller.AppId} for {caller.OnBehalfOf}"
        : "unstamped";
}
