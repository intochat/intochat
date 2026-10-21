using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Google.Gmail;

public sealed record GmailModuleOptions : IModuleOptions
{
    public Uri ProfileEndpoint { get; set; } = new("https://gmail.googleapis.com/gmail/v1/users/me/profile");
    public Uri DriveEndpoint { get; set; } = new("https://www.googleapis.com/drive/v3/");
    public bool HostGmail { get; set; }

    public GmailModuleOptions WithGmail() { HostGmail = true; return this; }

    public void Validate()
    {
        if (!ProfileEndpoint.IsAbsoluteUri || ProfileEndpoint.Scheme is not ("http" or "https"))
        { throw new ArgumentException("Google profile endpoint must be an absolute HTTP URL."); }
        if (!DriveEndpoint.IsAbsoluteUri || DriveEndpoint.Scheme is not ("http" or "https") || !DriveEndpoint.AbsolutePath.EndsWith('/'))
        { throw new ArgumentException("Google Drive endpoint must be an absolute HTTP URL ending in '/'."); }
    }
}
