using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Google.Gmail;

public sealed record GmailModuleOptions : IModuleOptions
{
    public Uri TokenEndpoint { get; set; } = new("https://oauth2.googleapis.com/token");
    public Uri? PublicOrigin { get; set; }
    public Uri ProfileEndpoint { get; set; } = new("https://gmail.googleapis.com/gmail/v1/users/me/profile");
    public bool HostGmail { get; set; }

    public GmailModuleOptions WithGmail() { HostGmail = true; return this; }
    public GmailModuleOptions WithTokenEndpoint(Uri endpoint) { TokenEndpoint = endpoint; return this; }

    public void Validate()
    {
        if (PublicOrigin is { } origin && (!origin.IsAbsoluteUri || origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback)
            || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0))
        { throw new ArgumentException("Gmail public origin must be an HTTPS origin or a loopback HTTP origin."); }
        if (!ProfileEndpoint.IsAbsoluteUri || ProfileEndpoint.Scheme is not ("http" or "https"))
        { throw new ArgumentException("Google profile endpoint must be an absolute HTTP URL."); }
        if (!TokenEndpoint.IsAbsoluteUri || TokenEndpoint.Scheme is not ("http" or "https"))
        { throw new ArgumentException("Google token endpoint must be an absolute HTTP URL."); }
    }
}
