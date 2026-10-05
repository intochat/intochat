using System.Diagnostics;
using DigitalBrain.Google.Gmail;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
namespace DigitalBrain.Modules.Google.Gmail.Tests;
public sealed class GmailOAuthPrivacyFacts
{
    [Fact]
    public async Task CallbackMiddlewareRemovesQueryTokensFromActivityBeforeAndAfterHandling()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        new GmailOAuthPrivacySurface().Map(app);
        app.Run(context =>
        {
            Assert.Equal("canary-code", context.Request.Query["code"]);
            Assert.Null(Activity.Current!.GetTagItem("url.query"));
            Activity.Current.SetTag("url.query", "late-canary");
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new("example.test");
        context.Request.Path = "/google/gmail/oauth/callback";
        context.Request.QueryString = new("?code=canary-code&state=canary-state");
        using var activity = new Activity("oauth-privacy").Start();
        activity.SetTag("url.query", "canary-code");
        await app.Build()(context);
        Assert.Null(activity.GetTagItem("url.query"));
        Assert.Equal("https://example.test/google/gmail/oauth/callback", activity.GetTagItem("url.full"));
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }
}
