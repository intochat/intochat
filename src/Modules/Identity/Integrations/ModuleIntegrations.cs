using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Integrations;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Host.Modules;
using IntoChat.OS.Modules.Identity.Contracts.Integrations;

namespace IntoChat.OS.Modules.Identity.Integrations;

internal sealed class ModuleIntegrations<TModule>(IServiceProvider services, ModuleInventory inventory) : IModuleIntegrations<TModule> where TModule : class
{
    public IIntegrationHandle Get(string integrationId)
    {
        if (!inventory.Types.Contains(typeof(TModule)) || !(IntegrationDiscovery.Declared(typeof(TModule)).Any(s => s.Id == integrationId)
            || ConnectorDefinition.DeclaredBy(typeof(TModule))
                .Any(c => c.RequiredIntegrations.Contains(integrationId, StringComparer.Ordinal) || c.OptionalIntegrations.Contains(integrationId, StringComparer.Ordinal))))
        { throw new UnauthorizedAccessException($"Module '{typeof(TModule).Name}' has not declared Integration '{integrationId}'."); }
        return new IntegrationHandle(integrationId, services.GetRequiredService<AuthorityNeurons>(), services.GetRequiredService<IReadOnlyList<IntegrationSchema>>(), services.GetRequiredService<EdgeAuthority>());
    }
}

internal sealed class IntegrationHandle(string integrationId, AuthorityNeurons neurons, IReadOnlyList<IntegrationSchema> schemas, EdgeAuthority edge) : IIntegrationHandle
{
    public string IntegrationId => integrationId;
    private IIntegration Integration => neurons.Integration(integrationId);
    public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        => Integration.Read().WaitAsync(cancellationToken);

    public async Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!schemas.Any(s => s.Id == integrationId && s.SecretFields.Concat(s.OptionalSecretFields).Contains(field, StringComparer.Ordinal)))
        { throw new UnauthorizedAccessException("The requested secret field is not declared by this Integration."); }
        // Release the whole Integration coherently, even for one field, rather than tear credentials across rotations.
        var released = await ReleaseAsync(cancellationToken).ConfigureAwait(false);
        return released.Values[field];
    }

    public async Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await edge.AsKernel(BrainId.Kernel, () => Integration.Release().WaitAsync(cancellationToken)).ConfigureAwait(false);
    }
}
