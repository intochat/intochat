using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Http;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Edge;

internal sealed class CSharpRuntimeInvocationDispatcher(RuntimeEdge edge) : IRuntimeInvocationDispatcher
{
    public Task<JsonElement?> DispatchAsync(string brainId, RuntimeInvocation invocation, CancellationToken cancellationToken)
    {
        return edge.InvokeAsync(brainId, null, invocation, cancellationToken);
    }
}
