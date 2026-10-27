using Microsoft.AspNetCore.Http;

namespace IntoChat.Cloud.Circle.Server;

// D-09 proof of possession for every machine request: an X-Machine-Proof signed by the credential's bound key,
// bound to this method, URL and token, inside the 60 s skew window, and with a nonce neither this silo nor the
// credential has seen. The kernel's runtime machine scheme is not reused: it authenticates the other direction.
internal static class MachineAuthentication
{
    public const string Scheme = "IntoChatMachine";

    public static string? Bearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && header.Length > prefix.Length ? header[prefix.Length..].Trim() : null;
    }

    public static async Task<bool> ProofHolds(HttpContext http, byte[] publicKeySpki, string boundToken, IMachineCredential credential, INonceCache nonces)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credential);
        var proof = http.Request.Headers[CircleRoutes.MachineProofHeader].ToString();
        if (proof.Length == 0) { return false; }
        var capture = new CapturingNonces(nonces);
        var result = MachineProof.Verify(publicKeySpki, proof, http.Request.Method, CircleResponses.RequestUrl(http.Request), boundToken,
            CircleServerModule.Clock(http.RequestServices).GetUtcNow(), capture);
        return result.IsValid && capture.Nonce is { } nonce && await credential.UseNonce(nonce);
    }
}
