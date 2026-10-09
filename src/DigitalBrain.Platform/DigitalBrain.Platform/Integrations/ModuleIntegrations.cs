using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Framework.Platform.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Integrations;

internal sealed class ModuleIntegrations<TModule>(IServiceProvider services, ModuleInventory inventory) : IModuleIntegrations<TModule> where TModule : Module
{
    public IIntegrationHandle Get(string integrationId)
    {
        if (!inventory.Types.Contains(typeof(TModule)) || !(IntegrationDiscovery.Declared(typeof(TModule)).Any(s => s.Id == integrationId)
            || ConnectorDefinition.DeclaredBy(typeof(TModule))
                .Any(c => c.RequiredIntegrations.Contains(integrationId, StringComparer.Ordinal) || c.OptionalIntegrations.Contains(integrationId, StringComparer.Ordinal))))
        { throw new UnauthorizedAccessException($"Module '{typeof(TModule).Name}' has not declared Integration '{integrationId}'."); }
        return new IntegrationHandle(integrationId, services.GetRequiredService<IGrainFactory>(), services.GetRequiredService<IReadOnlyList<IntegrationSchema>>());
    }
}

internal sealed class IntegrationHandle(string integrationId, IGrainFactory grains, IReadOnlyList<IntegrationSchema> schemas) : IIntegrationHandle
{
    public string IntegrationId => integrationId;
    private IIntegration Integration => grains.GetGrain<IIntegration>(IntegrationNeuron.KeyPrefix + integrationId);
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
        return await Integration.Release(IntegrationNeuron.PlatformCaller).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
