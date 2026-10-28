using System.Net;
using System.Text.RegularExpressions;

namespace IntoChat.Cloud.Mail.Templates;

internal sealed partial class MailTemplateBuilder(string template)
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public MailTemplateBuilder WithText(string placeholder, string value)
    {
        _values[placeholder] = WebUtility.HtmlEncode(value);
        return this;
    }

    public MailTemplateBuilder WithHtml(string placeholder, string html)
    {
        _values[placeholder] = html;
        return this;
    }

    public string Build() =>
        PlaceholderPattern().Replace(template, match =>
            _values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new InvalidOperationException($"Unresolved placeholder {match.Value}"));

    [GeneratedRegex(@"\{\{([A-Z_]+)\}\}")]
    private static partial Regex PlaceholderPattern();
}
