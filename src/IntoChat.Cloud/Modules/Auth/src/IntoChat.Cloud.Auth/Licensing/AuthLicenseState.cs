namespace IntoChat.Cloud.Auth.Licensing;

// What the running module knows of its licence: holder, expiry and a closed status. Never the token.
internal sealed class AuthLicenseState
{
    private readonly object gate = new();
    private string holder = "";
    private DateTimeOffset? expiresAt;
    private string status = "unverified";

    public (string Holder, DateTimeOffset? ExpiresAt, string Status) Read()
    {
        lock (gate)
        {
            return (holder, expiresAt, status);
        }
    }

    public void Set(string newHolder, DateTimeOffset? newExpiresAt, string newStatus)
    {
        lock (gate)
        {
            holder = newHolder;
            expiresAt = newExpiresAt;
            status = newStatus;
        }
    }
}
