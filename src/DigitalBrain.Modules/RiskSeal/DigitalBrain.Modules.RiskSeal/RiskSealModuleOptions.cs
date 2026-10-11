using DigitalBrain.Framework.Modules;

namespace DigitalBrain.RiskSeal;

public sealed record RiskSealModuleOptions : IModuleOptions
{
    // ASSUMED: RiskSeal has not published its contract or sandbox yet.
    public static readonly Uri DefaultBaseUrl = new("https://api.riskseal.io/");

    public Uri? BaseUrl { get; set; }

    // A loopback base URL is a local protocol fixture; it must be chosen explicitly.
    public bool AllowLoopback { get; set; }

    public void Validate()
    {
        if (BaseUrl is { } url) { _ = Resolve(url); }
    }

    // The integration's BaseUrl setting, when present, wins over the option.
    internal Uri Resolve(string? integrationSetting)
        => Resolve(string.IsNullOrWhiteSpace(integrationSetting) ? BaseUrl ?? DefaultBaseUrl
            : Uri.TryCreate(integrationSetting, UriKind.Absolute, out var setting) ? setting
            : throw new InvalidOperationException("The RiskSeal BaseUrl setting must be an absolute URL."));

    private Uri Resolve(Uri url)
    {
        if (!url.IsAbsoluteUri || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
        { throw new InvalidOperationException("The RiskSeal base URL must be an absolute URL without credentials, query or fragment."); }
        var local = AllowLoopback && url.IsLoopback && url.Scheme is "http" or "https";
        if (!local && url.Scheme != Uri.UriSchemeHttps)
        { throw new InvalidOperationException("The RiskSeal base URL must use HTTPS."); }
        return url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
    }
}
