using DigitalBrain.Capabilities.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Identity.External;

// Verifies a trusted issuer's service assertion on one edge request and spends its jti in the identity module's replay ledger,
// kept apart from the sign-in tickets' ledger by issuer key.
internal sealed class ServiceAssertions(TrustedIssuerKeys issuers, AuthorityNeurons neurons, TimeProvider clock, ILogger<ServiceAssertions> log) : IServiceAssertions
{
    private const string LedgerPrefix = "service:";

    public async Task<string?> Verify(string? authorization, string operation, string method, string pathAndQuery, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var prefix = ServiceAssertion.Scheme + " ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { return null; }
        var claims = ServiceAssertion.Verify(authorization[prefix.Length..].Trim(), issuers.ServicesOf, clock);
        if (claims is null)
        {
            log.LogInformation("Service assertion refused for {Operation}: it did not verify", operation);
            return null;
        }
        if (!string.Equals(claims.Operation, operation, StringComparison.Ordinal)
            || !string.Equals(claims.Request, ServiceAssertion.Request(method, pathAndQuery, body.Span), StringComparison.Ordinal))
        {
            log.LogWarning("Service assertion from {Issuer} refused: it names {Asserted}, not this {Operation} request", claims.Issuer, claims.Operation, operation);
            return null;
        }
        bool fresh;
        // The ledger is the identity module's own: the assertion is not a caller, and nothing is stamped before it is spent.
        using (neurons.Own())
        {
            fresh = await neurons.ExternalTicketReplay(LedgerPrefix + claims.Issuer, claims.Jti).Consume(claims.Jti, claims.ExpiresAt).WaitAsync(cancellationToken);
        }
        if (!fresh)
        {
            log.LogWarning("Service assertion from {Issuer} refused for {Operation}: it was already used", claims.Issuer, operation);
            return null;
        }
        return claims.Issuer;
    }
}
