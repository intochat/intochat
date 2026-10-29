using System.Security.Cryptography;
using System.Text;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Identity;
using IntoChat.Cloud.Auth.Neurons;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Services;

// identity@1 sessions and description for the Auth module (D-08): the host's SessionsCapability checks the privileged grant and
// the claims, then calls this port. A session id is "{base64url principal}.{32 random bytes}": the prefix routes to the
// principal's sessions neuron, which holds only the id's keyed hash and the member it was issued for (CV-3).
// The cloud kernel binds AddIdentitySessions over this port. The public constructor takes IServiceProvider so the kernel
// does not see AuthCalls or AuthHashing. The kernel registers it before this module's TryAdd.
public sealed class AuthIdentityProvider : IIdentitySessionPort
{
    private readonly AuthCalls calls;
    private readonly AuthHashing hashing;
    private readonly AuthModuleOptions options;

    public AuthIdentityProvider(IServiceProvider services)
        : this(services.GetRequiredService<AuthCalls>(), services.GetRequiredService<AuthHashing>(), services.GetRequiredService<AuthModuleOptions>())
    {
    }

    internal AuthIdentityProvider(AuthCalls calls, AuthHashing hashing, AuthModuleOptions options)
    {
        this.calls = calls;
        this.hashing = hashing;
        this.options = options;
    }

    public const string ProviderId = "auth";

    public async Task<SessionIssued> Issue(CapabilityCaller caller, SessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var member = request.Member;
        var sessionId = Base64Url(Encoding.UTF8.GetBytes(member.PrincipalId)) + "." + Base64Url(RandomNumberGenerator.GetBytes(32));
        var created = await calls.Sessions(member.PrincipalId, sessions => sessions.Issue(new SessionOpen(
            hashing.SidHash(sessionId), hashing.SessionHandle(sessionId), "Other", request.Lifetime ?? options.SessionLifetime,
            member)));
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
        return row;
    }

    // The Settings Account tab mounts this neuron (kind|key = view|surface). The kernel writes that setting only under Topology=cloud.
    public static readonly NeuronId AccountSurfaceId = new(new DigitalBrain.BrainId("account"), "view", "surface");

    public const string AccountSurfaceSetting = "view|surface";

    public IdentityProviderDescriptor Describe() => new()
    {
        ProviderId = ProviderId,
        Posture = options.Posture,
        AccountSurface = AccountSurfaceId,
    };

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
