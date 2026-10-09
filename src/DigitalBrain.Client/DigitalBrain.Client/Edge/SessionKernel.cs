using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Client.Runtime;
using DigitalBrain.Client.Runtime.V1;

namespace DigitalBrain.Client;

// A behavior's brain.Kernel: each question is one invocation in the behavior's name on the kernel's
// reserved contract. The edge answers for the behavior's own brain only.
internal sealed class SessionKernel(SessionConnection session, string behavior, BehaviorTrace trace) : IKernelView
{
    public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron
        => Listed(RuntimeProtocol.KernelInstances, [typeof(T).FullName, null], cancellationToken);

    public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default)
        => Listed(RuntimeProtocol.KernelInstances, [null, type], cancellationToken);

    public async Task<Instance[]> Running(CancellationToken cancellationToken = default)
        => await Ask(RuntimeProtocol.KernelRunning, [], cancellationToken).ConfigureAwait(false);

    private async IAsyncEnumerable<Instance> Listed(string method, string?[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var instance in await Ask(method, arguments, cancellationToken).ConfigureAwait(false)) { yield return instance; }
    }

    private async Task<Instance[]> Ask(string method, string?[] arguments, CancellationToken cancellationToken)
    {
        var invocation = new RuntimeInvocation(behavior, RuntimeProtocol.KernelContract, session.Brain, method,
            [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument, RuntimeProtocol.Json))]);
        var value = await session.InvokeAsync(invocation, trace.TraceParent, cancellationToken).ConfigureAwait(false);
        return value is { } json ? json.Deserialize<Instance[]>(RuntimeProtocol.Json) ?? [] : [];
    }
}
