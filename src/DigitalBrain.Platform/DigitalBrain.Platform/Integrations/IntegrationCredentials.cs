using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Framework.Platform.Secrets;
using DigitalBrain.Framework;
using DigitalBrain.Platform.Secrets;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Integrations;

internal sealed class IntegrationCredentials(IGrainFactory grains, ILogger<IntegrationCredentials> logger)
{
    public Task<SecretRef> Store(IntegrationSchema definition, string field, string value)
        => grains.GetGrain<ISecrets>(IntegrationVault.Owner).Set(Authorize(definition, field, "store"),
            IntegrationVault.SecretName(definition.Id, field), $"{definition.Id} {field}", value);

    public Task Remove(IntegrationSchema definition, string field)
        => grains.GetGrain<ISecrets>(IntegrationVault.Owner).Remove(Authorize(definition, field, "remove"), IntegrationVault.SecretName(definition.Id, field));

    private CallerContext Authorize(IntegrationSchema definition, string field, string operation)
    {
        if (!definition.AllFields.Contains(field, StringComparer.Ordinal)) { throw new ArgumentException("Unknown registration field.", nameof(field)); }
        var actor = CallerContextStamper.TryGet(out var current) ? current : new CallerContext
        {
            PrincipalId = IntegrationVault.CallerAppId,
            AccountId = IntegrationVault.Owner,
            BrainId = IntegrationVault.Owner,
            Kind = CallerKind.Platform,
            StampedBy = TrustedEdge.Platform
        };
        if (actor.Kind == CallerKind.App || actor.StampedBy == TrustedEdge.AppProxy)
        { throw new UnauthorizedAccessException("Installed apps cannot configure provider registrations."); }
        logger.LogInformation("Registration {Operation} for {Provider}/{Field} requested by {Actor} in {Account}/{Brain}.",
            operation, definition.Id, field, actor.PrincipalId, actor.AccountId, actor.BrainId);
        return actor with { Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform, AppId = IntegrationVault.CallerAppId };
    }
}
