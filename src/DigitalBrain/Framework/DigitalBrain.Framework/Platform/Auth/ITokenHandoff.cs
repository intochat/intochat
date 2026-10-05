using System.Diagnostics.CodeAnalysis;
namespace DigitalBrain.Framework.Platform.Auth;

public interface ITokenHandoff
{
    string Deposit(OAuthTokens tokens);
    bool TryTake(string nonce, [MaybeNullWhen(false)] out OAuthTokens tokens);
}
