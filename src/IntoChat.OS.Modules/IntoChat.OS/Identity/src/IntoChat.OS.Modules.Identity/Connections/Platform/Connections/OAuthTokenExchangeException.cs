using DigitalBrain.Capabilities.Connections.Connectors;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Platform.Connections;

public enum OAuthTokenFailure { ReconsentRequired, Unavailable, InvalidResponse }

public sealed class OAuthTokenExchangeException(OAuthTokenFailure failure) : InvalidOperationException(failure switch
{
    OAuthTokenFailure.ReconsentRequired => "The provider refused the grant. Connect the account again.",
    OAuthTokenFailure.InvalidResponse => "The provider returned an invalid token response.",
    _ => "The token provider is unavailable. Try again later.",
})
{
    public OAuthTokenFailure Failure { get; } = failure;
}
