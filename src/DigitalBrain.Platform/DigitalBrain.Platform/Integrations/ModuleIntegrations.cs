using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Integrations.Accounts;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Platform.Enforcement;
using DigitalBrain.Framework.Types;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Integrations;

internal sealed class ModuleIntegrations<TModule>(IServiceProvider services, ModuleInventory inventory) : IModuleIntegrations<TModule> where TModule : Module
{
    public IIntegrationHandle Get(string integrationId)
    {
        if (!inventory.Types.Contains(typeof(TModule)) || !(IntegrationDiscovery.Declared(typeof(TModule)).Any(s => s.Id == integrationId)
            || ConnectorDiscovery.Declared(typeof(TModule))
                .Any(c => c.RequiredIntegrations.Contains(integrationId, StringComparer.Ordinal) || c.OptionalIntegrations.Contains(integrationId, StringComparer.Ordinal))))
        { throw new UnauthorizedAccessException($"Module '{typeof(TModule).Name}' has not declared Integration '{integrationId}'."); }
        return new IntegrationHandle(integrationId, services.GetRequiredService<IGrainFactory>(), services.GetRequiredService<IReadOnlyList<IntegrationSchema>>());
    }
}

internal sealed class IntegrationHandle(string integrationId, IGrainFactory grains, IReadOnlyList<IntegrationSchema> schemas) : IIntegrationHandle
{
    public string IntegrationId => integrationId;
    private IIntegration Integration => grains.GetGrain<IIntegration>(IntegrationVault.GrainKeyPrefix + integrationId);
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
        var caller = new CallerContext
        {
            PrincipalId = IntegrationVault.CallerAppId,
            AccountId = IntegrationVault.Owner,
            BrainId = IntegrationVault.Owner,
            Kind = CallerKind.Platform,
            StampedBy = TrustedEdge.Platform,
            AppId = IntegrationVault.CallerAppId,
        };
        return await Integration.Release(caller).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class ModuleConnectionCredentials<TModule>(IGrainFactory grains, ModuleInventory inventory, DigitalBrain.Framework.Platform.Identity.IIdentity identity) : IModuleConnectionCredentials<TModule> where TModule : Module
{
    public async Task ConnectAsync(string connectionKey, string authMethodId, SecretRef credential, CancellationToken cancellationToken = default, string? targetBrainId = null)
    {
        var (brain, connector, connection) = ConnectionKeys.Parse(connectionKey);
        RequireConnector(connector);
        var caller = CallerContextStamper.Require();
        var target = targetBrainId ?? caller.BrainId;
        if (brain != BrainScope.Create(caller.AccountId, target).Id || caller.Kind == CallerKind.App
            || !await identity.CanAccessAsync(caller.PrincipalId, caller.AccountId, target, cancellationToken))
        { throw new UnauthorizedAccessException("Only an authorized brain member can connect this credential."); }
        using var _ = CallerContextScope.Scoped(caller with { BrainId = target });
        await grains.GetGrain<IConnectionRegistry>(brain).Connect(new ConnectConnection
        {
            IntegrationId = connector,
            ConnectionId = connection,
            AuthMethodId = authMethodId,
            SecretReference = credential.Reference,
        }, cancellationToken);
    }

    public async Task<bool> IsConnectedAsync(string connectionKey, CancellationToken cancellationToken = default)
    {
        var (brain, connector, connection) = ConnectionKeys.Parse(connectionKey);
        RequireConnector(connector);
        var caller = CallerContextStamper.Require();
        if (brain != BrainScope.Create(caller.AccountId, caller.BrainId).Id || caller.Kind == CallerKind.App
            || !await identity.CanAccessAsync(caller.PrincipalId, caller.AccountId, caller.BrainId, cancellationToken))
        { throw new UnauthorizedAccessException("Only an authorized brain member can probe this connection."); }
        using var _ = CallerContextScope.Scoped(caller with { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = connector });
        return (await grains.GetGrain<IConnectionRegistry>(brain).List(cancellationToken))
            .Any(c => c.Id == connection && c.IntegrationId == connector && c.Status == ConnectionStatus.Connected);
    }

    private void RequireConnector(string connectorId)
    {
        if (!inventory.Types.Contains(typeof(TModule)) || !ConnectorDiscovery.Declared(typeof(TModule)).Any(c => c.Id == connectorId))
        { throw new UnauthorizedAccessException("The module does not declare this connector."); }
    }

    public async Task<SecretRef> WriteSecretAsync(string connectionKey, string field, string displayName, string value, CancellationToken cancellationToken = default, string? targetBrainId = null)
    {
        var (brain, connector, _) = ConnectionKeys.Parse(connectionKey);
        RequireConnector(connector);
        var caller = CallerContextStamper.Require();
        var target = targetBrainId ?? caller.BrainId;
        if (brain != BrainScope.Create(caller.AccountId, target).Id || caller.Kind == CallerKind.App
            || !await identity.CanAccessAsync(caller.PrincipalId, caller.AccountId, target, cancellationToken))
        { throw new UnauthorizedAccessException("Connection credentials require the owning brain's authenticated caller."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var path = "connection." + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(connectionKey + "\0" + field)));
        return await grains.GetGrain<ISecrets>(caller.PrincipalId).Set(caller with { BrainId = target, Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = connector }, path, displayName, value, cancellationToken);
    }

    public async Task<string> ReadConnectionAsync(string connectorId, string connectionId, CancellationToken cancellationToken = default)
    {
        RequireConnector(connectorId);
        var caller = CallerContextStamper.Require();
        var scope = BrainScope.Create(caller.AccountId, caller.BrainId);
        var connections = await grains.GetGrain<IConnectionRegistry>(scope.Id).List(cancellationToken);
        var connection = connections.SingleOrDefault(c => c.Id == connectionId && c.IntegrationId == connectorId && c.WorkspaceId == caller.BrainId)
            ?? throw new UnauthorizedAccessException("The connection does not belong to this brain and connector.");
        if (connection.Status != ConnectionStatus.Connected) { throw new InvalidOperationException("The connection is unavailable."); }
        return await grains.GetGrain<ISecrets>(caller.PrincipalId).Resolve(caller with { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = connectorId }, connection.Credential, cancellationToken);
    }
}
