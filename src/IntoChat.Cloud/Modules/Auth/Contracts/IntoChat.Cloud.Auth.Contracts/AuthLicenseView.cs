namespace IntoChat.Cloud.Auth;

// What an operator sees of the installation's Auth licence; never the token.
public sealed record AuthLicenseView(
    string Holder,
    DateTimeOffset ExpiresAt,
    int DaysLeft,
    string Status);
