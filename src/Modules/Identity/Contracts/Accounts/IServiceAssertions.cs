namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

// A trusted issuer calling this install's edge on its own behalf (DigitalBrain.Capabilities.Identity.ServiceAssertion): the edge
// passes the request as it arrived, never an identity taken from it. Only an issuer listed with Services is trusted for it.
public interface IServiceAssertions
{
    // The issuer whose assertion admits exactly this request for this operation, or null. The assertion must be in
    // "Authorization: Service <token>", signed by the issuer for this install's audience, unexpired, name this operation and bind
    // this method, path with query and body. Its jti is spent here, so the same assertion is admitted once.
    Task<string?> Verify(string? authorization, string operation, string method, string pathAndQuery, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}
