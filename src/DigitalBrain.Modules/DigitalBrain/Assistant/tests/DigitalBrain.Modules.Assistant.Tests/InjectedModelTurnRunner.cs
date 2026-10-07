using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Assistant.Tests;

// Only the model catalog is swapped for one that resolves to the host client, selecting the injected
// deterministic IChatClient. All tool dispatch, context, and event handling stay real.
internal sealed class InjectedModelTurnRunner(IServiceProvider services) : IAgentTurnRunner, IDisposable
{
    private readonly ServiceProvider _hostOnly = new ServiceCollection().AddModelCatalog()
        .AddSingleton(services.GetRequiredService<IChatClient>()).BuildServiceProvider();
    public void Dispose() => _hostOnly.Dispose();
    public ConcurrentQueue<string> CompletedTools { get; } = new();
    public ConcurrentQueue<string> Failures { get; } = new();
    public async IAsyncEnumerable<AgentTurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in new AgentTurnRunner(new InjectedModelServices(services, _hostOnly.GetRequiredService<ModelCatalog>())).RunAsync(request with { Model = null }, ct))
        {
            if (item is AgentTurnEvent.Failed failure) { Failures.Enqueue(failure.Message); }
            if (item is AgentTurnEvent.ToolCompleted tool) { CompletedTools.Enqueue(tool.Name); }
            yield return item;
        }
    }
    private sealed class InjectedModelServices(IServiceProvider original, ModelCatalog catalog) : IServiceProvider
    {
        public object? GetService(Type type) => type == typeof(ModelCatalog) ? catalog : original.GetService(type);
    }
}
