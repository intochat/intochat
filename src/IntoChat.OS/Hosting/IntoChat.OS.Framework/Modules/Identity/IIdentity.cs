using DigitalBrain.Capabilities;
namespace IntoChat.OS.Framework.Modules.Platform.Identity;

public interface IIdentity
{
    CapabilityCaller? CurrentPrincipal { get; }

    string RequirePrincipal();

    ValueTask<bool> CanAccessAsync(string principalId, string accountId, string brainId, CancellationToken cancellationToken = default);

    ValueTask<bool> HasGrantAsync(CapabilityCaller caller, string semanticTypeId, CancellationToken cancellationToken = default);
}
