using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Equifax;

public sealed record EquifaxModuleOptions : IModuleOptions
{
    // ASSUMED: the Equifax Peru sandbox host is pending; see README.
    public static readonly Uri DefaultBaseUrl = new("https://api.sandbox.equifax.com/");
    public const string DefaultScope = "https://api.equifax.com/business/consumer-credit/v1";

    public Uri? BaseUrl { get; set; } = DefaultBaseUrl;
    public string Scope { get; set; } = DefaultScope;

    // A loopback base URL is a local protocol fixture; it must be chosen explicitly.
    public bool AllowLoopback { get; set; }

    public void Validate()
    {
        if (BaseUrl is not null) { _ = Resolve(BaseUrl); }
        if (string.IsNullOrWhiteSpace(Scope)) { throw new ArgumentException("Equifax needs an OAuth scope."); }
    }

    // A base URL from options, the integration or a pasted credential: HTTPS, or loopback HTTP when allowed.
    internal Uri Resolve(Uri? candidate)
    {
        var uri = candidate ?? BaseUrl ?? DefaultBaseUrl;
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        { throw new ArgumentException("An Equifax base URL must be absolute, without credentials, query or fragment."); }
        var allowed = uri.Scheme == Uri.UriSchemeHttps || AllowLoopback && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
        if (!allowed) { throw new ArgumentException("An Equifax base URL must use HTTPS."); }
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}
