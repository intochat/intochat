using System.Text;
using System.Text.RegularExpressions;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Microsoft.Playwright;

// The browser stays on the server. The app publishes observations and evidence through generic text.
public static class ResearchView
{
    public static Task Begin(DigitalBrainConnection brain, string key, string query) =>
        Show(brain, key, "## Researching " + Plain(query) + "\n\nStarting an isolated browser session. Observed pages will appear here as research proceeds.");

    public static Task Observed(DigitalBrainConnection brain, string key, string query, IReadOnlyList<BrowserObservation> pages)
    {
        var text = new StringBuilder("## Sources for ").Append(Plain(query))
            .Append("\n\nObserved page content · findings are still being checked.\n\n");
        foreach (var page in pages.GroupBy(page => page.Url).Select(group => group.Last()).TakeLast(4))
        {
            text.Append("### ").Append(Plain(string.IsNullOrWhiteSpace(page.Title) ? page.Url : page.Title))
                .Append("\n\n").Append(Link(page.Url)).Append("\n\n");
            var excerpt = Regex.Replace(page.Text, @"\s+", " ").Trim();
            text.Append(Plain(excerpt[..Math.Min(excerpt.Length, 700)]))
                .Append(excerpt.Length > 700 ? "…" : "").Append("\n\n");
        }
        return Show(brain, key, text.ToString());
    }

    public static Task Saved(DigitalBrainConnection brain, string key, string query, Company company)
    {
        var text = new StringBuilder("## Saved: ").Append(Plain(company.CompanyName))
            .Append("\n\nResearch for ").Append(Plain(query))
            .Append(". Every finding below was matched to an observed official page. Unknown details remain empty.\n\n");
        foreach (var evidence in company.Evidence)
        {
            var label = evidence.Field switch
            {
                "companyName" => "Company", "website" => "Website", "location" => "Location",
                "email" => "Email", "phone" => "Phone", "industry" => "Industry", "summary" => "Summary",
                _ => evidence.Field,
            };
            text.Append("### ").Append(label).Append("\n\n").Append(Plain(evidence.Value))
                .Append("\n\n").Append(Link(evidence.Url)).Append("\n\n> ")
                .Append(Plain(Regex.Replace(evidence.Quote, @"\s+", " "))).Append("\n\n");
        }
        return Show(brain, key, text.ToString());
    }

    static Task Show(DigitalBrainConnection brain, string key, string markdown) =>
        brain.Get<IText>(key + "/live-evidence").Set(markdown);

    // Page text is data, not Markdown; do not let it add links, images or headings to the surface.
    static string Plain(string value) => Regex.Replace(Regex.Replace(value, @"\s+", " "), @"([\\`*_\[\]<>#])", @"\$1");
    static string Link(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        ? "[Open source](<" + uri.AbsoluteUri.Replace("<", "%3C").Replace(">", "%3E") + ">)"
        : Plain(url);
}
