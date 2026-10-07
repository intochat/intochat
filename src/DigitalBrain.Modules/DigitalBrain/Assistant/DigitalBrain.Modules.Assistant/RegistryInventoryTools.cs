using System.ComponentModel;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Assistant;

internal sealed class RegistryInventoryTools(IRegistry registry) : IAgentToolFactory
{
    public IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context)
    {
        void RequireScope()
        {
            if (context().ScopeId != BrainScope.CurrentId())
            { throw new UnauthorizedAccessException("Inventory must use the caller's current brain."); }
        }
        Task<RegistryInventory> Persisted(CancellationToken ct,
            [Description("Optional exact grain type ID, for example reminder, timer, or apps.app.")] string? typeId = null)
        { RequireScope(); return registry.Persisted(typeId, ct); }
        Task<RegistryInventory> Active(CancellationToken ct,
            [Description("Optional exact grain type ID.")] string? typeId = null)
        { RequireScope(); return registry.Active(typeId, ct); }
        async Task<RegistryInventory> Synapses(CancellationToken ct)
        {
            RequireScope();
            var all = await registry.Persisted(null, ct);
            // Programs that run are neurons of their own kind; today the C# file is the only one.
            var synapseTypes = new HashSet<string>(StringComparer.Ordinal) { "microsoft.csharp.file" };
            return all with { Records = all.Records.Where(record => synapseTypes.Contains(record.TypeId)).ToArray() };
        }
        return [
            AIFunctionFactory.Create(Synapses, "list_synapses", "List the current brain's synapses: durable programs that watch neurons and act. Records are attributed to their owning Domain (apps for app-owned synapses, csharp for standalone files)."),
            AIFunctionFactory.Create(Persisted, "list_neurons", "List the current brain's domain-owned inventory. Attribute Records to Domain; Domains names everyone asked and Errors names failures. An absent domain is not evidence that it has no resources. Use discover_capabilities for rich follow-up operations."),
            AIFunctionFactory.Create(Active, "list_active_neurons", "List domain-owned neurons active at the runtime census. Report Domains and Errors alongside Records; failures are not empty inventories."),
        ];
    }
}
