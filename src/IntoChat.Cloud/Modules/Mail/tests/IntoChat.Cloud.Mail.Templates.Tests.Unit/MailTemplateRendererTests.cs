using System.Text.Json;
using System.Text.RegularExpressions;

namespace IntoChat.Cloud.Mail.Templates.Tests.Unit;

public sealed class MailTemplateRendererTests
{
    private static readonly MailTemplateRenderer Renderer = new();

    private static readonly Dictionary<string, string> FullData = new()
    {
        [MailDataKeys.Code] = "123456",
        [MailDataKeys.InviterName] = "Anna Berg",
        [MailDataKeys.OrganisationName] = "Nordlys",
        [MailDataKeys.MemberName] = "Ola Nord",
        [MailDataKeys.UserName] = "ola@example.com",
        [MailDataKeys.ActionUrl] = "https://app.intochat.io/accept?token=abc&x=1",
    };

    public static TheoryData<MailTemplateId, string> EveryTemplateAndLanguage()
    {
        var data = new TheoryData<MailTemplateId, string>();
        foreach (var template in Enum.GetValues<MailTemplateId>())
        {
            foreach (var language in MailTemplateRenderer.Languages)
            {
                data.Add(template, language);
            }
        }

        return data;
    }

    public static TheoryData<MailTemplateId, string> SecurityTemplates()
    {
        var data = new TheoryData<MailTemplateId, string>();
        MailTemplateId[] ids = [MailTemplateId.SignupCode, MailTemplateId.LoginCode, MailTemplateId.ResetCode, MailTemplateId.ChangeEmailCode, MailTemplateId.AccountAlreadyExists, MailTemplateId.TeamInvite];
        foreach (var id in ids)
        {
            foreach (var language in MailTemplateRenderer.Languages)
            {
                data.Add(id, language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTemplateAndLanguage))]
    public void Render_EveryTemplateAndLanguage_HasNoUnresolvedPlaceholders(MailTemplateId template, string language)
    {
        var mail = Renderer.Render(template, language, FullData);

        Assert.False(string.IsNullOrWhiteSpace(mail.Subject));
        Assert.False(string.IsNullOrWhiteSpace(mail.PlainText));
        Assert.DoesNotContain("{{", mail.Html);
        Assert.DoesNotContain("{{", mail.PlainText);
        Assert.DoesNotContain("{0}", mail.Html);
        Assert.DoesNotContain("TripRadar", mail.Html);
    }

    [Theory]
    [InlineData(MailTemplateId.SignupCode, "en", "Your code: 123456")]
    [InlineData(MailTemplateId.SignupCode, "ru", "Ваш код: 123456")]
    [InlineData(MailTemplateId.LoginCode, "en", "Your code: 123456")]
    [InlineData(MailTemplateId.ResetCode, "ru", "Ваш код: 123456")]
    [InlineData(MailTemplateId.ChangeEmailCode, "en", "Your code: 123456")]
    public void Render_CodeTemplate_HeadingMatches(MailTemplateId template, string language, string expected)
    {
        var mail = Renderer.Render(template, language, FullData);

        Assert.Contains($">{expected}</h2>", mail.Html);
        Assert.StartsWith(expected, mail.PlainText);
    }

    [Theory]
    [InlineData("en", "Anna Berg invited you to join Nordlys")]
    [InlineData("ru", "Anna Berg приглашает вас в Nordlys")]
    public void Render_TeamInvite_HeadingMatches(string language, string expected)
    {
        var mail = Renderer.Render(MailTemplateId.TeamInvite, language, FullData);

        Assert.Contains($">{expected}</h2>", mail.Html);
    }

    [Theory]
    [InlineData("en", "Ola Nord joined Nordlys")]
    [InlineData("ru", "Ola Nord присоединился к Nordlys")]
    public void Render_InviteAccepted_HeadingMatches(string language, string expected)
    {
        var mail = Renderer.Render(MailTemplateId.InviteAccepted, language, FullData);

        Assert.Contains($">{expected}</h2>", mail.Html);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Render_ScriptInInviterName_IsHtmlEncoded(string language)
    {
        var data = new Dictionary<string, string>(FullData) { [MailDataKeys.InviterName] = "<script>alert(1)</script>" };

        var mail = Renderer.Render(MailTemplateId.TeamInvite, language, data);

        Assert.DoesNotContain("<script>", mail.Html);
        Assert.Contains("&lt;script&gt;", mail.Html);
    }

    [Fact]
    public void Render_PlaceholderInOrganisationName_IsNotExpanded()
    {
        var data = new Dictionary<string, string>(FullData) { [MailDataKeys.OrganisationName] = "{{ACTION_URL}}" };

        var mail = Renderer.Render(MailTemplateId.TeamInvite, "en", data);

        Assert.Contains("join {{ACTION_URL}}</h2>", mail.Html);
    }

    [Theory]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("xx-YY")]
    public void Render_UnknownLanguage_FallsBackToEnglish(string language)
    {
        var fallback = Renderer.Render(MailTemplateId.SignupCode, language, FullData);
        var english = Renderer.Render(MailTemplateId.SignupCode, "en", FullData);

        Assert.Equal(english, fallback);
    }

    [Fact]
    public void Localisation_EnAndRu_HaveSameKeys()
    {
        var en = Flatten(ReadLocalisation("en"));
        var ru = Flatten(ReadLocalisation("ru"));

        Assert.Equal(en.Keys.Order(), ru.Keys.Order());
        foreach (var (key, value) in en.Where(pair => pair.Value.Contains('{')))
        {
            Assert.Equal(ArgumentCount(value), ArgumentCount(ru[key]));
        }
    }

    [Theory]
    [MemberData(nameof(SecurityTemplates))]
    public void Render_SecurityTemplates_HaveNoUnsubscribeLink(MailTemplateId template, string language)
    {
        var mail = Renderer.Render(template, language, FullData);

        Assert.DoesNotContain("nsubscribe", mail.Html);
        Assert.DoesNotContain("Отписаться", mail.Html);
        Assert.DoesNotContain("unsubscribe-", mail.Html);
    }

    [Fact]
    public void Render_TeamInvite_ContainsActionUrl()
    {
        var mail = Renderer.Render(MailTemplateId.TeamInvite, "en", FullData);

        Assert.Contains("href=\"https://app.intochat.io/accept?token=abc&amp;x=1\"", mail.Html);
        Assert.Contains("https://app.intochat.io/accept?token=abc&x=1", mail.PlainText);
    }

    [Fact]
    public void Render_FooterCopyright_UsesProvidedClock()
    {
        var clock = new FixedClock(new DateTimeOffset(2031, 5, 1, 0, 0, 0, TimeSpan.Zero));

        var mail = new MailTemplateRenderer(clock).Render(MailTemplateId.LoginCode, "en", FullData);

        Assert.Contains("2031 IntoChat", mail.Html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative")]
    public void Render_NonHttpActionUrl_Throws(string url)
    {
        var data = new Dictionary<string, string>(FullData) { [MailDataKeys.ActionUrl] = url };

        Assert.Throws<ArgumentException>(() => Renderer.Render(MailTemplateId.TeamInvite, "en", data));
    }

    [Fact]
    public void Render_MissingCode_Throws()
    {
        var data = new Dictionary<string, string>(FullData);
        data.Remove(MailDataKeys.Code);

        Assert.Throws<ArgumentException>(() => Renderer.Render(MailTemplateId.SignupCode, "en", data));
    }

    private static JsonElement ReadLocalisation(string language)
    {
        var assembly = typeof(MailTemplateRenderer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith($"Templates.{language}.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    private static Dictionary<string, string> Flatten(JsonElement element, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var (key, value) in Flatten(property.Value, $"{prefix}{property.Name}."))
                    {
                        result[key] = value;
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var (key, value) in Flatten(item, $"{prefix}{index++}."))
                    {
                        result[key] = value;
                    }
                }

                break;
            default:
                result[prefix.TrimEnd('.')] = element.GetString() ?? string.Empty;
                break;
        }

        return result;
    }

    private static int ArgumentCount(string value) => Regex.Matches(value, @"\{\d+\}").Select(m => m.Value).Distinct().Count();

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
