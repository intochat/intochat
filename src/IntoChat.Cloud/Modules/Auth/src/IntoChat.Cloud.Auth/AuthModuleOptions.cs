
namespace IntoChat.Cloud.Auth;

public sealed class AuthModuleOptions
{
    // The licence token. Read by AuthLicenseCheck from configuration; never printed.
    public string? License { get; set; }

    // Absolute http(s) origin the code mails link to; required once Auth is licensed and registered.
    public string? PublicBaseUrl { get; set; }

    // Principal ids allowed to read GET /auth/license; empty answers 404 to everyone.
    public string[] Operators { get; set; } = [];

    public int PasswordIterations { get; set; } = 600_000;
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan RecentAuthWindow { get; set; } = TimeSpan.FromMinutes(10);
    public int LoginLockoutThreshold { get; set; } = 5;
    public TimeSpan LoginLockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan AuditRetention { get; set; } = TimeSpan.FromDays(365);
    public int AuditMaxEntries { get; set; } = 10_000;
    public int IpThrottleLimit { get; set; } = 20;
    public TimeSpan IpThrottleWindow { get; set; } = TimeSpan.FromMinutes(10);
    public string[] AllowedReturnOrigins { get; set; } = [];

    // Public origins of OS installs that trust this cloud as the issuer "intochat-cloud". A sign-in whose return URL
    // is on one of them ends at {install}/identity/external with a signed ticket instead of a direct redirect.
    public string[] OsInstalls { get; set; } = [];
    public int ExpiryWarningDays { get; set; } = 14;

    // Open (anyone reaching the install is the local owner) or Secured. A licensed Auth refuses to start unless Secured.
    public DigitalBrain.Capabilities.Identity.IdentityPosture Posture { get; set; } = DigitalBrain.Capabilities.Identity.IdentityPosture.Open;

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(PublicBaseUrl)
            && (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUrl) || (baseUrl.Scheme != Uri.UriSchemeHttps && baseUrl.Scheme != Uri.UriSchemeHttp)))
        {
            throw new ArgumentException("Auth PublicBaseUrl must be an absolute http or https URL.");
        }

        if (OsInstalls.Any(install => !Uri.TryCreate(install, UriKind.Absolute, out var origin)
            || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp) || origin.AbsolutePath != "/" || origin.Query.Length > 0))
        {
            throw new ArgumentException("Auth OsInstalls must list absolute http or https origins.");
        }

        if (PasswordIterations < 1 || LoginLockoutThreshold < 1 || AuditMaxEntries < 1 || IpThrottleLimit < 1 || ExpiryWarningDays < 0)
        {
            throw new ArgumentException("Auth numeric options must be positive.");
        }

        if (SessionLifetime <= TimeSpan.Zero || RecentAuthWindow <= TimeSpan.Zero || LoginLockoutDuration <= TimeSpan.Zero
            || AuditRetention <= TimeSpan.Zero || IpThrottleWindow <= TimeSpan.Zero)
        {
            throw new ArgumentException("Auth durations must be positive.");
        }
    }

    // The licence token is never part of the printed options.
    public override string ToString() =>
        $"AuthModuleOptions {{ PublicBaseUrl = {PublicBaseUrl}, Operators = {Operators.Length}, SessionLifetime = {SessionLifetime} }}";
}
