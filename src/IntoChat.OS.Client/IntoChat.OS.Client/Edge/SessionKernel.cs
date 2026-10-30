using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using IntoChat.OS.Client.Runtime;
using IntoChat.OS.Client.Runtime.V1;

namespace IntoChat.OS.Client;

// A behavior's brain.Kernel: each question is one invocation in the behavior's name on the kernel's
// reserved contract. The edge answers for the behavior's own brain only.
public sealed class SessionKernel
{
    private readonly SessionConnection session;
    private readonly string behavior;
    private readonly BehaviorTrace trace;

    internal SessionKernel(SessionConnection session, string behavior, BehaviorTrace trace)
    {
        this.session = session;
        this.behavior = behavior;
        this.trace = trace;
    }

    public IAsyncEnumerable<BrainInstance> Instances<T>(CancellationToken cancellationToken) where T : class
        => Listed(RuntimeProtocol.KernelInstances, [typeof(T).FullName, null], cancellationToken);

    public IAsyncEnumerable<BrainInstance> Instances(string? type, CancellationToken cancellationToken = default)
        => Listed(RuntimeProtocol.KernelInstances, [null, type], cancellationToken);

    public Task<BrainInstance[]> Running(CancellationToken cancellationToken = default)
        => Ask<BrainInstance>(RuntimeProtocol.KernelRunning, [], cancellationToken);

    public Task<string[]> Brains(CancellationToken cancellationToken = default)
        => Ask<string>(RuntimeProtocol.KernelBrains, [], cancellationToken);

    public Task<BrainType[]> Types(CancellationToken cancellationToken = default)
        => Ask<BrainType>(RuntimeProtocol.KernelTypes, [], cancellationToken);

    private async IAsyncEnumerable<BrainInstance> Listed(string method, string?[] arguments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var instance in await Ask<BrainInstance>(method, arguments, cancellationToken).ConfigureAwait(false)) { yield return instance; }
    }

    private async Task<T[]> Ask<T>(string method, string?[] arguments, CancellationToken cancellationToken)
    {
        var invocation = new RuntimeInvocation(behavior, RuntimeProtocol.KernelContract, session.Brain, method,
            [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument, RuntimeProtocol.Json))]);
        var value = await session.InvokeAsync(invocation, trace.TraceParent, cancellationToken).ConfigureAwait(false);
        return value is { } json ? json.Deserialize<T[]>(RuntimeProtocol.Json) ?? [] : [];
    }
}
