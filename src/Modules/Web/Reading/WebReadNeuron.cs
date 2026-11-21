using DigitalBrain;
using DigitalBrain.Capabilities.Http;
using DigitalBrain.Log;
using DigitalBrain.Neurons;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Microsoft.Playwright;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Web.Reading;

[Neuron("web.read"), StatelessNeuron]
internal sealed class WebReadNeuron(
    IHttpClientFactory? clients = null,
    IOptions<WebModuleOptions>? options = null) : Neuron, IWebRead
{
    private const int MinLettersBeforeBrowserFallback = 400;
    private static readonly HttpClient FallbackClient = new(HostBoundHttp.Handler(VettedConnect.Dns)) { Timeout = HostBoundHttp.Timeout };

    public async Task<WebPage> Read(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        EnsureNotGoogleSearch(url);

        var http = new HostBoundHttp(clients?.CreateClient(WebModule.PublicHttpClientName) ?? FallbackClient);
        var fetched = await http.GetPublic(url, CancellationToken.None).ConfigureAwait(false);
        EnsureNotGoogleSearch(fetched.FinalUrl);

        var finalUrl = fetched.FinalUrl;
        var mediaType = (fetched.MediaType ?? "text/plain").Trim().ToLowerInvariant();
        string title;
        string markdown;

        switch (mediaType)
        {
            case "text/html" or "application/xhtml+xml":
                (title, markdown) = HtmlMarkdown.Convert(fetched.Text, finalUrl);
                if (CountLetters(markdown) < MinLettersBeforeBrowserFallback
                    && fetched.Text.Contains("<script", StringComparison.OrdinalIgnoreCase))
                {
                    (finalUrl, title, markdown) = await TryPlaywrightFallback(finalUrl, title, markdown).ConfigureAwait(false);
                }

                break;

            case "text/plain" or "text/markdown":
                title = string.Empty;
                markdown = fetched.Text;
                break;

            case var json when json == "application/json" || json.EndsWith("+json", StringComparison.Ordinal):
                title = string.Empty;
                markdown = "```json\n" + fetched.Text + "\n```";
                break;

            default:
                throw new ArgumentException($"{finalUrl} is {fetched.MediaType ?? mediaType}; only HTML, text and JSON pages can be read.", nameof(url));
        }

        var maxChars = options?.Value.MaxMarkdownChars is > 0 ? options.Value.MaxMarkdownChars : 32_000;
        markdown = Clip(markdown, maxChars);

        return new WebPage(finalUrl, title, UntrustedText.Fence(finalUrl, markdown));
    }

    private async Task<(Uri Url, string Title, string Markdown)> TryPlaywrightFallback(Uri url, string title, string markdown)
    {
        try
        {
            var browser = Get<IPlaywright>(new NeuronId(Id.Brain, "playwright", "web/browser"));
            await browser.Start().ConfigureAwait(false);
            var observed = await browser.Navigate(url.AbsoluteUri).ConfigureAwait(false);
            var resolvedUrl = Uri.TryCreate(observed.Url, UriKind.Absolute, out var parsed) ? parsed : url;
            EnsureNotGoogleSearch(resolvedUrl);
            var resolvedTitle = !string.IsNullOrWhiteSpace(observed.Title) ? observed.Title : title;
            var resolvedMarkdown = !string.IsNullOrWhiteSpace(observed.Text) ? observed.Text : markdown;
            return (resolvedUrl, resolvedTitle, resolvedMarkdown);
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<WebReadNeuron>().LogSwallowed(error, "Playwright fallback failed during web.read; returning HTTP Markdown");
            return (url, title, markdown);
        }
    }

    internal static bool IsGoogleSearchUrl(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var path = url.AbsolutePath;
        if (!path.Equals("/search", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/search/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }
        else if (host.StartsWith("m.", StringComparison.Ordinal))
        {
            host = host[2..];
        }
        else if (host.StartsWith("encrypted.", StringComparison.Ordinal))
        {
            host = host[10..];
        }

        if (!host.StartsWith("google.", StringComparison.Ordinal))
        {
            return false;
        }

        var tld = host["google.".Length..];
        if (tld.Length == 0 || tld.StartsWith('.') || tld.EndsWith('.'))
        {
            return false;
        }

        var labels = tld.Split('.');
        return labels.Length == 1
            || (labels.Length == 2 && labels[0] is "co" or "com" or "org" or "net" or "ne" or "or" or "ac");
    }

    private static void EnsureNotGoogleSearch(Uri url)
    {
        if (IsGoogleSearchUrl(url))
        {
            throw new ArgumentException("Google search pages cannot be read; use web search instead.", nameof(url));
        }
    }

    private static int CountLetters(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                count++;
            }
        }

        return count;
    }

    private static string Clip(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..cut] + $"\n[… clipped at {maxChars} characters]";
    }
}
