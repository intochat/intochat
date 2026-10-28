using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.Cloud.Mail.Templates;

public sealed record RenderedMail(string Subject, string Html, string PlainText);

public sealed partial class MailTemplateRenderer(TimeProvider? timeProvider = null)
{
    private const string DefaultLanguage = "en";
    private const string FeaturesListStyle = "margin:0;padding-left:20px;color:#374151;";

    private static readonly string[] SupportedLanguages = ["en", "ru"];
    private static readonly string Layout = ReadResource("layout.html");
    private static readonly Dictionary<string, JsonElement> Localisations = SupportedLanguages.ToDictionary(
        language => language,
        language => JsonDocument.Parse(ReadResource($"{language}.json")).RootElement);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static IReadOnlyList<string> Languages => SupportedLanguages;

    public RenderedMail Render(MailTemplateId template, string language, IReadOnlyDictionary<string, string> data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var resolvedLanguage = ResolveLanguage(language);
        var root = Localisations[resolvedLanguage];
        var section = root.GetProperty(template.ToString());
        var common = root.GetProperty("Common");
        var actionUrl = RequireActionUrl(data);
        var headingArguments = HeadingArguments(template, data);

        var heading = string.Format(
            CultureInfo.InvariantCulture,
            Text(section, "MainHeading"),
            headingArguments.Select(WebUtility.HtmlEncode).Cast<object>().ToArray());
        var plainHeading = string.Format(
            CultureInfo.InvariantCulture,
            Text(section, "MainHeading"),
            headingArguments.Cast<object>().ToArray());
        var features = section.GetProperty("Features").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        var copyright = string.Format(CultureInfo.InvariantCulture, Text(common, "FooterCopyright"), _timeProvider.GetUtcNow().Year);

        var html = new MailTemplateBuilder(UnsubscribeBlock().Replace(Layout, string.Empty))
            .WithText(MailPlaceholders.Lang, resolvedLanguage)
            .WithText(MailPlaceholders.Title, Text(section, "Title"))
            .WithText(MailPlaceholders.HeaderColor, Text(common.GetProperty("HeaderColors"), template.ToString()))
            .WithHtml(MailPlaceholders.MainHeading, heading)
            .WithText(MailPlaceholders.Username, data.GetValueOrDefault(MailDataKeys.UserName) ?? string.Empty)
            .WithText(MailPlaceholders.Message, Text(section, "Message"))
            .WithText(MailPlaceholders.ButtonText, Text(section, "ButtonText"))
            .WithText(MailPlaceholders.ButtonColor, Text(common.GetProperty("ButtonColors"), template.ToString()))
            .WithText(MailPlaceholders.ButtonHoverColor, Text(common.GetProperty("ButtonHoverColors"), template.ToString()))
            .WithText(MailPlaceholders.ActionUrl, actionUrl)
            .WithText(MailPlaceholders.SecondaryMessage, Text(section, "SecondaryMessage"))
            .WithHtml(MailPlaceholders.Features, BuildFeatureList(features))
            .WithText(MailPlaceholders.ExpiryInfo, Text(section, "ExpiryInfo"))
            .WithText(MailPlaceholders.IgnoreText, Text(section, "IgnoreText"))
            .WithText(MailPlaceholders.Greeting, Text(common, "Greeting"))
            .WithText(MailPlaceholders.UrlFallbackLabel, Text(common, "UrlFallbackLabel"))
            .WithText(MailPlaceholders.FooterBestRegards, Text(common, "FooterBestRegards"))
            .WithText(MailPlaceholders.FooterTeamName, Text(common, "FooterTeamName"))
            .WithText(MailPlaceholders.FooterCopyright, copyright)
            .WithText(MailPlaceholders.FooterTagline, Text(common, "FooterTagline"))
            .WithHtml(MailPlaceholders.SocialMediaLinks, string.Empty)
            .WithText(MailPlaceholders.CompanyAddress, Text(common, "CompanyAddress"))
            .WithHtml(MailPlaceholders.LogoUrl, string.Empty)
            .WithHtml(MailPlaceholders.SignatureImageUrl, string.Empty)
            .Build();

        var plainText = BuildPlainText(plainHeading, section, common, features, actionUrl);
        return new RenderedMail(Text(section, "Subject"), html, plainText);
    }

    private static string BuildPlainText(string heading, JsonElement section, JsonElement common, string[] features, string actionUrl)
    {
        var builder = new StringBuilder();
        builder.AppendLine(StripTags(heading)).AppendLine();
        builder.AppendLine(StripTags(Text(section, "Message"))).AppendLine();
        builder.AppendLine($"{StripTags(Text(section, "ButtonText"))}: {actionUrl}").AppendLine();
        builder.AppendLine(StripTags(Text(section, "SecondaryMessage")));
        foreach (var feature in features)
        {
            builder.AppendLine($"- {StripTags(feature)}");
        }

        var expiry = StripTags(Text(section, "ExpiryInfo"));
        if (!string.IsNullOrWhiteSpace(expiry))
        {
            builder.AppendLine().AppendLine(expiry);
        }

        builder.AppendLine().AppendLine(StripTags(Text(section, "IgnoreText")));
        builder.AppendLine().AppendLine(Text(common, "FooterBestRegards")).Append(Text(common, "FooterTeamName"));
        return builder.ToString();
    }

    private static string[] HeadingArguments(MailTemplateId template, IReadOnlyDictionary<string, string> data) => template switch
    {
        MailTemplateId.SignupCode or MailTemplateId.LoginCode or MailTemplateId.ResetCode or MailTemplateId.ChangeEmailCode
            => [Require(data, MailDataKeys.Code)],
        MailTemplateId.TeamInvite
            => [Require(data, MailDataKeys.InviterName), Require(data, MailDataKeys.OrganisationName)],
        MailTemplateId.InviteAccepted
            => [Require(data, MailDataKeys.MemberName), Require(data, MailDataKeys.OrganisationName)],
        _ => [],
    };

    private static string RequireActionUrl(IReadOnlyDictionary<string, string> data)
    {
        var url = Require(data, MailDataKeys.ActionUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("actionUrl must be an absolute http or https URL", nameof(data));
        }

        return uri.AbsoluteUri;
    }

    private static string Require(IReadOnlyDictionary<string, string> data, string key) =>
        data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing mail data key '{key}'", nameof(data));

    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultLanguage;
        }

        var primary = language.Trim().Split('-', '_')[0].ToLowerInvariant();
        return SupportedLanguages.Contains(primary) ? primary : DefaultLanguage;
    }

    private static string BuildFeatureList(IEnumerable<string> items)
    {
        var builder = new StringBuilder($"<ul style='{FeaturesListStyle}'>");
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            builder.Append("<li>").Append(WebUtility.HtmlEncode(item)).Append("</li>");
        }

        return builder.Append("</ul>").ToString();
    }

    private static string Text(JsonElement parent, string name) => parent.GetProperty(name).GetString() ?? string.Empty;

    private static string StripTags(string value) => WebUtility.HtmlDecode(TagPattern().Replace(value, string.Empty)).Trim();

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(MailTemplateRenderer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($"Templates.{fileName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {fileName} not found");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex("<!--unsubscribe-start-->.*?<!--unsubscribe-end-->", RegexOptions.Singleline)]
    private static partial Regex UnsubscribeBlock();
}
