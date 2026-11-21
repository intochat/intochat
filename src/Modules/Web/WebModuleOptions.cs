using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Web;

public sealed class WebModuleOptions : IModuleOptions
{
    [Setting]
    public SearchProvider Provider { get; set; } = SearchProvider.Off;

    [Setting]
    public Uri? SearxngUrl { get; set; }

    [Setting]
    public int MaxMarkdownChars { get; set; } = 32_000;

    public void Validate()
    {
        if (MaxMarkdownChars <= 0)
        {
            throw new ArgumentException("MaxMarkdownChars must be positive.");
        }

        if (Provider == SearchProvider.Searxng)
        {
            if (SearxngUrl is null || !SearxngUrl.IsAbsoluteUri || SearxngUrl.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("SearxngUrl must be an absolute http(s) URL when Provider is Searxng.");
            }
        }
        else if (SearxngUrl is not null && (!SearxngUrl.IsAbsoluteUri || SearxngUrl.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("SearxngUrl must be an absolute http(s) URL when set.");
        }
    }
}
