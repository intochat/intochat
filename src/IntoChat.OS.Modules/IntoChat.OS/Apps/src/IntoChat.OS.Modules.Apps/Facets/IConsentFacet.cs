using Orleans;
using Orleans.CodeGeneration;

namespace IntoChat.OS.Modules.Apps.Facets;

[Alias("apps.consent-facet")]
[Version(1)]
public interface IConsentFacet : IGrainExtension
{
    Task<bool> HasConsentAsync(string capabilityId);
    Task RequestConsentAsync(string capabilityId, string reason);
    Task<bool> ReviewStagedActionAsync(Guid operationId, bool approved);
}
