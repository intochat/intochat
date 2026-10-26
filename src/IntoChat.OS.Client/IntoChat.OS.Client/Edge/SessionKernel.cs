using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client;

// A behavior's brain.Kernel: each question is one invocation in the behavior's name on the kernel's
// reserved contract. The edge answers for the behavior's own brain only.
internal sealed class SessionKernel(SessionConnection session, string behavior, BehaviorTrace trace) : IKernelView
{
    public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : class
        => Listed(RuntimeProtocol.KernelInstances, [typeof(T).FullName, null], cancellationToken);

    public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default)
        => Listed(RuntimeProtocol.KernelInstances, [null, type], cancellationToken);

    public Task<Instance[]> Running(CancellationToken cancellationToken = default)
        => Ask<Instance>(RuntimeProtocol.KernelRunning, [], cancellationToken);

    public Task<string[]> Brains(CancellationToken cancellationToken = default)
        => Ask<string>(RuntimeProtocol.KernelBrains, [], cancellationToken);

    public Task<TypeEntry[]> Types(CancellationToken cancellationToken = default)
        => Ask<TypeEntry>(RuntimeProtocol.KernelTypes, [], cancellationToken);

    private async IAsyncEnumerable<Instance> Listed(string method, string?[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var instance in await Ask<Instance>(method, arguments, cancellationToken).ConfigureAwait(false)) { yield return instance; }
    }

    private async Task<T[]> Ask<T>(string method, string?[] arguments, CancellationToken cancellationToken)
    {
        var invocation = new RuntimeInvocation(behavior, RuntimeProtocol.KernelContract, session.Brain, method,
            [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument, RuntimeProtocol.Json))]);
        var value = await session.InvokeAsync(invocation, trace.TraceParent, cancellationToken).ConfigureAwait(false);
        return value is { } json ? json.Deserialize<T[]>(RuntimeProtocol.Json) ?? [] : [];
    }
}
