using DigitalBrain;
using DigitalBrain.Neurons;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Entries;
using Orleans;

namespace IntoChat.OS.Modules.AI.Models;

// A brain's catalog is {brain}/ai.models/main; the deployment's view is the system brain's (R4-19).
[Neuron("ai.models"), StatelessNeuron]
internal sealed class ModelCatalogNeuron(ModelCatalog catalog, IGrainFactory? grains = null) : DigitalBrain.Neurons.Neuron, IModelCatalog
{
    private BrainId? Brain => Id.Brain.Value == IModelCatalog.SystemBrain ? null : Id.Brain;

    public Task<ModelCard[]> List(Abilities need = Abilities.None)
        => catalog.List(need, CancellationToken.None);

    public Task<ModelId> Pick(Role role, ModelId? turn = null)
        => catalog.Pick(Brain, role, turn, ReadPersonSettingsAsync, CancellationToken.None);

    public async Task SetRole(Role role, ModelId model)
    {
        var isDevicesModule = Context.Caller is { Actor: DigitalBrain.Access.Actor.Module { Id: "devices" }, Brain: var callerBrain }
            && callerBrain == Id.Brain;
        if (!isDevicesModule)
        {
            EnforceOwnerOrTrustedShell("SetRole", "Only the brain owner may change model roles.", "Only the person or a built-in shell screen may change model roles.");
        }

        if (Brain is null)
        {
            throw new InvalidOperationException("Setting a model role requires a brain scope.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(model.Value);
        await Get<IUserSettings>(new NeuronId(Id.Brain, "os.settings", "settings")).SetRole(role, model).ConfigureAwait(false);
    }

    public Task<RoleMap> Roles()
        => catalog.Roles(Brain, ReadPersonSettingsAsync, CancellationToken.None);

    public Task<ModelAvailability[]> Availability()
        => catalog.Availability(Brain, CancellationToken.None);

    public async Task<LocalModelSummary> DetectLocal()
    {
        var detected = await catalog.DetectLocal(CancellationToken.None).ConfigureAwait(false);
        return detected is null
            ? new LocalModelSummary(false, null, [])
            : new LocalModelSummary(true, detected.PreferredModel, detected.InstalledModels);
    }

    public async Task SaveProviderCredential(string provider, string credentialValue)
    {
        EnforceOwnerOrTrustedShell("SaveProviderCredential", "Only the brain owner may store provider credentials.", "Only the person or a built-in shell screen may save provider credentials.");

        if (Brain is not { Value: var brainId })
        {
            throw new InvalidOperationException("Provider credentials require a brain scope.");
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("A provider identifier is required.", nameof(provider));
        }

        var normalized = provider.Trim().ToLowerInvariant();
        if (!BrainEntries.KeyedProviderIds.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported key-based provider '{provider}'.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(credentialValue))
        {
            throw new ArgumentException("A non-empty credential value is required.", nameof(credentialValue));
        }

        if (grains is null)
        {
            throw new InvalidOperationException("Platform entry storage is unavailable in this host.");
        }

        await grains.GetGrain<IBrainEntries>(brainId)
            .Put(BrainEntries.ProviderEntry(normalized), EntryKind.Secret, credentialValue.Trim())
            .ConfigureAwait(false);
    }

    private void EnforceOwnerOrTrustedShell(string operation, string agentMessage, string appMessage)
    {
        if (Context.Caller is not { } caller)
        {
            return;
        }

        if (caller.Actor is DigitalBrain.Access.Actor.Agent)
        {
            throw new DigitalBrain.Rules.RuleViolationException(
                new DigitalBrain.Rules.Rejected(operation, ["owner-only"], [agentMessage]));
        }

        if (caller.Actor is DigitalBrain.Access.Actor.App && !IsTrustedShellCaller(caller, Brain?.Value))
        {
            throw new UnauthorizedAccessException(appMessage);
        }
    }

    private async Task<UserSettingsState?> ReadPersonSettingsAsync(CancellationToken _)
    {
        if (Brain is null)
        {
            return null;
        }

        try
        {
            return await Get<IUserSettings>(new NeuronId(Id.Brain, "os.settings", "settings")).Read().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    // A built-in shell screen ("app.*") acting in this brain; a stored or proxied app carries its own key.
    private static bool IsTrustedShellCaller(DigitalBrain.Access.Caller caller, string? brain)
        => brain is not null
            && string.Equals(caller.Brain.Value, brain, StringComparison.Ordinal)
            && caller.Actor is DigitalBrain.Access.Actor.App { Key: var appId }
            && appId.StartsWith("app.", StringComparison.Ordinal);
}
