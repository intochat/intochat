using System.Security.Cryptography;
using System.Text;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Neurons;

namespace IntoChat.Cloud.Auth.Services;

// identity@1 sessions and description for the Auth module (D-08): the host's SessionsCapability checks the privileged grant and
// the claims, then calls this port. A session id is "{base64url principal}.{32 random bytes}": the prefix routes to the
// principal's sessions neuron, which holds only the id's keyed hash and the member it was issued for (CV-3).
internal sealed class AuthIdentityProvider(AuthCalls calls, AuthHashing hashing, AuthModuleOptions options) : IIdentitySessionPort
{
    public const string ProviderId = "auth";

    public async Task<SessionIssued> Issue(CapabilityCaller caller, SessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var member = request.Member;
        var sessionId = Base64Url(Encoding.UTF8.GetBytes(member.PrincipalId)) + "." + Base64Url(RandomNumberGenerator.GetBytes(32));
        var created = await calls.Sessions(member.PrincipalId, sessions => sessions.Issue(new SessionOpen(
            hashing.SidHash(sessionId), hashing.SessionHandle(sessionId), "Other", request.Lifetime ?? options.SessionLifetime,
            new SessionMember(member.PrincipalId, member.AccountId, member.BrainId, member.Role.ToString(), member.DisplayName))));
        return new SessionIssued(sessionId, created.ExpiresAt);
    }

    public async Task End(CapabilityCaller caller, string sessionId, CancellationToken cancellationToken)
    {
        if (PrincipalOf(sessionId) is not { } principal) { return; }
        await calls.Sessions(principal, sessions => sessions.RevokeCurrent(hashing.SidHash(sessionId)));
    }

    public async Task<Member?> Validate(string sessionId, CancellationToken cancellationToken)
    {
        if (PrincipalOf(sessionId) is not { } principal) { return null; }
        var row = await calls.Sessions(principal, sessions => sessions.Find(hashing.SidHash(sessionId)));
        return row is null || !Enum.TryParse<MemberRole>(row.Role, out var role) ? null : new Member
        {
            PrincipalId = row.PrincipalId,
            AccountId = row.AccountId,
            BrainId = row.BrainId,
            Role = role,
            DisplayName = row.DisplayName,
        };
    }

    public IdentityProviderDescriptor Describe() => new() { ProviderId = ProviderId, Posture = options.Posture };

    // The principal a session id names, or null for an id Auth did not issue (never resolves a neuron for it).
    internal static string? PrincipalOf(string sessionId)
    {
        var dot = sessionId.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot > 256) { return null; }
        try { return Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(sessionId.AsSpan(0, dot))) is { Length: > 0 } principal ? principal : null; }
        catch (FormatException) { return null; }
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
