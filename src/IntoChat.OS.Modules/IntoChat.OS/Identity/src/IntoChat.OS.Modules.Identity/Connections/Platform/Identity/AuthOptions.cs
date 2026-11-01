using DigitalBrain.Capabilities;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Connections.Identity.Configuration;

// Open: single-tenant development and test — anonymous requests act as the synthetic "owner".
// Secured: cookie sessions plus an optional Basic bootstrap credential — anonymous gets 401.
public enum IdentityPosture
{
    Open,
    Secured,
}

// The host declares its security posture; it is never inferred from what a request carries or
// which keys happen to be configured, and a host that declares nothing refuses to start.
public sealed class AuthOptions
{
    public const string SectionName = "IntoChatOS:Auth";
    public const string PostureKey = SectionName + ":" + nameof(Posture);

    public static string Key(string property) => SectionName + ":" + property;

    public IdentityPosture? Posture { get; set; }

    // The Open posture's synthetic owner ("owner" signing in as "owner"), a person at the authenticated edge:
    // it owns its own brain before the brain's authority records any membership. Every owner rule asks this.
    public const string OpenOwner = "owner";

    public static bool IsOpenPostureOwner(IdentityPosture? posture, CapabilityCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return posture == IdentityPosture.Open && caller.Kind == CallerKind.User
            && caller.StampedBy != TrustedEdge.AppProxy
            && caller.PrincipalId == OpenOwner && caller.AccountId == OpenOwner;
    }
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Open posture serves every anonymous caller as the owner; in Aspire run mode it may listen on loopback only
    // (OpenPostureLoopback in ServiceDefaults) unless this explicit opt-in is set.
    public bool AllowOpenOnNetwork { get; set; }

    // External sign-in (GET /identity/external) is mapped only when at least one issuer is listed; the local password
    // posture stays as it is either way. Keys, per entry n:
    //   IntoChatOS:Auth:TrustedIssuers:n:Name      the issuer's name; the ticket's "kid" header and "iss" claim carry it.
    //                                             It also scopes the principals the issuer signs in, so it never changes.
    //   IntoChatOS:Auth:TrustedIssuers:n:PublicKey the issuer's P-256 public key, SubjectPublicKeyInfo DER in base64.
    //   IntoChatOS:Auth:TrustedIssuers:n:Audience  this install's audience as the issuer addresses it (its public origin,
    //                                             for example https://os.example.com); a ticket's "aud" must equal it.
    public TrustedIssuer[] TrustedIssuers { get; set; } = [];

    // Absolute return URLs external sign-in may redirect to (https origins); local paths are always allowed.
    //   IntoChatOS:Auth:ExternalReturnOrigins:n
    public string[] ExternalReturnOrigins { get; set; } = [];
}

public sealed class TrustedIssuer
{
    public string Name { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Audience { get; set; } = "";
}
