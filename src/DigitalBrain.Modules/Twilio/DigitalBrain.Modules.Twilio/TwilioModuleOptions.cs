using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Twilio;

public sealed record TwilioModuleOptions : IModuleOptions
{
    public static readonly Uri DefaultBaseUrl = new("https://api.twilio.com/");

    public Uri? BaseUrl { get; set; }

    // A loopback base URL is a local protocol fixture; it must be chosen explicitly.
    public bool AllowLoopback { get; set; }

    public void Validate() => _ = ResolveBaseUrl();

    internal Uri ResolveBaseUrl()
    {
        var uri = BaseUrl ?? DefaultBaseUrl;
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { throw new ArgumentException("The Twilio base URL must be an absolute URL without credentials, query or fragment."); }
        var local = AllowLoopback && uri.IsLoopback && uri.Scheme is "http" or "https";
        if (!local && uri.Scheme != Uri.UriSchemeHttps)
        { throw new ArgumentException("The Twilio base URL must use HTTPS; a loopback fixture needs AllowLoopback."); }
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
