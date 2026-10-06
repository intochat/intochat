using System.ComponentModel;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
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
        return [
            AIFunctionFactory.Create(Persisted, "list_neurons", "List the current brain's domain-owned inventory. Attribute Records to Domain; Domains names everyone asked and Errors names failures. An absent domain is not evidence that it has no resources. Use discover_capabilities for rich follow-up operations."),
            AIFunctionFactory.Create(Active, "list_active_neurons", "List domain-owned neurons active at the runtime census. Report Domains and Errors alongside Records; failures are not empty inventories."),
        ];
    }
}
