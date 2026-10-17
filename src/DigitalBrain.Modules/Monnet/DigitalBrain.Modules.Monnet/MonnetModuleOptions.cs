using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Monnet;

public sealed record MonnetModuleOptions : IModuleOptions
{
    // ASSUMED until Monnet publishes its API: the sandbox origin.
    public static readonly Uri DefaultBaseUrl = new("https://sandbox.api.monnet.io/");

    public Uri? BaseUrl { get; set; }

    // A loopback base URL is a local protocol fixture; it must be chosen explicitly.
    public bool AllowLoopback { get; set; }

    public void Validate()
    {
        if (BaseUrl is { } url) { _ = Resolve(url); }
    }

    // A credential's or integration's BaseUrl wins over the option; the option over the sandbox default.
    internal Uri Resolve(Uri? candidate)
    {
        var uri = candidate ?? BaseUrl ?? DefaultBaseUrl;
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { throw new MonnetUnavailableException("The Monnet base URL must be an absolute URL without credentials, query or fragment."); }
        var local = AllowLoopback && uri.IsLoopback && uri.Scheme is "http" or "https";
        if (!local && uri.Scheme != Uri.UriSchemeHttps)
        { throw new MonnetUnavailableException("The Monnet base URL must use HTTPS."); }
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
