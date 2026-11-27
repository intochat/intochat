namespace IntoChat.OS.Modules.Identity.Contracts.Accounts;

/// <summary>
/// Signs outbound ES256 service-assertion JWTs on behalf of this hosted OS install when calling a trusted
/// service (such as the cloud Circle relay) scoped to a specific brain and purpose.
/// </summary>
public interface IServiceAssertionSigner
{
    /// <summary>Whether outbound service-assertion signing is configured with a target service URI and P-256 key.</summary>
    bool IsConfigured { get; }

    /// <summary>The configured target service base URI, or null when not configured.</summary>
    Uri? ServiceUri { get; }

    /// <summary>
    /// Signs a short-lived ES256 service-assertion JWT scoped to <paramref name="brainId"/> and
    /// <paramref name="purpose"/>, cryptographically bound to the HTTP request method, path+query, and body.
    /// </summary>
    string Sign(string brainId, string purpose, string method, string pathAndQuery, ReadOnlySpan<byte> body);
}
