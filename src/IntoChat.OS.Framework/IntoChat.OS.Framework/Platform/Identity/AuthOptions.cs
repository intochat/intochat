namespace IntoChat.OS.Authority.Identity.Configuration;

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
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Open posture serves every anonymous caller as the owner; in Aspire run mode it may listen on loopback only
    // (OpenPostureLoopback in ServiceDefaults) unless this explicit opt-in is set.
    public bool AllowOpenOnNetwork { get; set; }
}
