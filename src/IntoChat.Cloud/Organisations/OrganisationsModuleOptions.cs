namespace IntoChat.Cloud.Organisations;

public sealed record OrganisationsModuleOptions
{
    public int MaxOrganisationNameLength { get; set; } = 100;
    public int MaxInvitesPerRequest { get; set; } = 20;
    public TimeSpan InviteLifetime { get; set; } = TimeSpan.FromDays(7);
    public string PublicBaseUrl { get; set; } = "https://app.intochat.io";
    public TimeSpan InviteResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        if (MaxOrganisationNameLength < 1) { throw new ArgumentException("MaxOrganisationNameLength must be positive."); }
        if (MaxInvitesPerRequest < 1) { throw new ArgumentException("MaxInvitesPerRequest must be positive."); }
        if (InviteLifetime <= TimeSpan.Zero) { throw new ArgumentException("InviteLifetime must be positive."); }
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttps && !(baseUrl.Scheme == Uri.UriSchemeHttp && baseUrl.IsLoopback)))
        {
            throw new ArgumentException("PublicBaseUrl must be an absolute https URL (http only for localhost).");
        }
        if (InviteResendCooldown < TimeSpan.Zero) { throw new ArgumentException("InviteResendCooldown must not be negative."); }
    }
}
