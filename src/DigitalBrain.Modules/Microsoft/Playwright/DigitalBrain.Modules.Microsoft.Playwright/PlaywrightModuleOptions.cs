using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Microsoft.Playwright;

// Origins of apps the deployment itself serves (e.g. ArtCraft web builds on loopback). Pages on these
// origins load directly instead of through the public-only proxy, and only they accept Evaluate.
public sealed class PlaywrightModuleOptions : IModuleOptions
{
    public string[] AppOrigins { get; set; } = [];

    public void Validate()
    {
        foreach (var origin in AppOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            { throw new ArgumentException($"App origin '{origin}' must be a bare http(s) origin."); }
        }
    }

    internal bool IsAppPage(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && AppOrigins.Any(origin => Uri.TryCreate(origin, UriKind.Absolute, out var allowed)
                && Uri.Compare(uri, allowed, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0);
}
