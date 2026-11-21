using DigitalBrain.Access;
using DigitalBrain.Contracts;
using IntoChat.OS;

namespace IntoChat.OS.Modules.AI.Models;

// The deployment's model list as a neuron, so other modules and the shell read it without
// referencing the AI module. Never carries endpoints or secrets.
[NeuronAlias("ai.models"), DefaultNeuronType("ai.models")]
[Public]
public interface IModelCatalog
{
    // A brain's catalog: its list and availability count that brain's own provider keys. The deployment's view is the
    // catalog of the reserved system brain (R4-19: a cluster-scoped singleton lives there, keyed main).
    static string Key(string brainId) => $"{brainId}/main";

    const string SystemBrain = "system";

    static string DeploymentKey => Key(SystemBrain);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<ModelCard[]> List(Abilities need = Abilities.None);

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<ModelId> Pick(Role role, ModelId? turn = null);

    // Person or built-in shell screen only (same check as SaveProviderCredential).
    Task SetRole(Role role, ModelId model);

    // Effective role map: person overrides over the deployment map.
    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<RoleMap> Roles();

    [DigitalBrain.Contracts.ReadOnlyCall, DigitalBrain.Contracts.Interleaves]
    Task<ModelAvailability[]> Availability();

    // Stores a brain-scoped provider credential in the durable platform entry store.
    Task SaveProviderCredential(string provider, string credentialValue);
}

public sealed record ModelAvailability(ModelId Model, bool Ready, string? Reason);
