using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Capabilities;
using Orleans;
using IntoChat.OS.Modules.Identity.Contracts.Entries;

namespace IntoChat.OS.Modules.AI.Models;

// A brain's catalog is {brain}/ai.models/main; the deployment's view is the system brain's (R4-19).
[Neuron("ai.models"), StatelessNeuron]
internal sealed class ModelCatalogNeuron(ModelCatalog catalog, IGrainFactory? grains = null) : DigitalBrain.Neurons.Neuron, IModelCatalog
{
    private string? Brain => Id.Brain.Value == IModelCatalog.SystemBrain ? null : Id.Brain.Value;

    public Task<ModelChoices> List(string? defaultModel = null) => catalog.List(Brain, defaultModel, CancellationToken.None);
    public Task<ModelSelection?> Select(string? id, string? defaultModel = null) => catalog.Select(Brain, id, defaultModel, CancellationToken.None);

    public async Task<LocalModelSummary> DetectLocal()
    {
        var detected = await catalog.DetectLocal(CancellationToken.None).ConfigureAwait(false);
        return detected is null
            ? new LocalModelSummary(false, null, [])
            : new LocalModelSummary(true, detected.PreferredModel, detected.InstalledModels);
    }

    public async Task SaveProviderCredential(string provider, string credentialValue)
    {
        if (Context.Caller is { } caller)
        {
            if (caller.Actor is DigitalBrain.Access.Actor.Agent)
            {
                throw new DigitalBrain.Rules.RuleViolationException(
                    new DigitalBrain.Rules.Rejected("SaveProviderCredential", ["owner-only"], ["Only the brain owner may store provider credentials."]));
            }

            if (caller.Actor is DigitalBrain.Access.Actor.App && !IsTrustedShellCaller(caller, Brain))
            {
                throw new UnauthorizedAccessException("Only the person or a built-in shell screen may save provider credentials.");
            }
        }

        if (Brain is null)
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

        await grains.GetGrain<IBrainEntries>(Brain)
            .Put(BrainEntries.ProviderEntry(normalized), EntryKind.Secret, credentialValue.Trim())
            .ConfigureAwait(false);
    }

    // A built-in shell screen ("app.*") acting in this brain; a stored or proxied app carries its own key.
    private static bool IsTrustedShellCaller(DigitalBrain.Access.Caller caller, string? brain)
        => brain is not null
            && string.Equals(caller.Brain.Value, brain, StringComparison.Ordinal)
            && caller.Actor is DigitalBrain.Access.Actor.App { Key: var appId }
            && appId.StartsWith("app.", StringComparison.Ordinal);
}
