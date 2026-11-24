using System.Security.Claims;
using IntoChat.OS.Modules.Microsoft.Playwright;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace IntoChat.OS.Kernel.Auth;

public sealed record InternalPageReport(
    string Path,
    int StatusCode,
    string Title,
    IReadOnlyList<string> ConsoleErrors)
{
    public bool IsHealthy => StatusCode is >= 200 and < 400 && ConsoleErrors.Count == 0;
}

public sealed class InternalPageInspector(
    IServer server,
    IConfiguration configuration,
    IOptions<PlaywrightModuleOptions> playwrightOptions,
    WebViewTickets tickets)
{
    private const string ConnectionsPublicOriginKey = "IntoChatOS:Connections:PublicOrigin";

    public async Task<InternalPageReport> InspectAsync(
        ClaimsPrincipal caller,
        string relativePath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!WebViewTicketEndpoints.IsSafeRelativeTarget(relativePath))
        {
            throw new ArgumentException("Invalid target path. Must be a safe relative path starting with '/'.", nameof(relativePath));
        }

        var origin = ResolveKernelOrigin();
        var enterUrl = tickets.Issue(caller, relativePath);
        var targetUri = new Uri(origin, enterUrl);

        using var playwright = await Playwright.CreateAsync().WaitAsync(ct);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = playwrightOptions.Value.ChromiumExecutablePath,
            Timeout = 15_000,
        }).WaitAsync(ct);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
        }).WaitAsync(ct);

        var page = await context.NewPageAsync().WaitAsync(ct);
        var consoleErrors = new List<string>();
        page.Console += (_, message) =>
        {
            if (string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                consoleErrors.Add(message.Text);
            }
        };

        var response = await page.GotoAsync(targetUri.AbsoluteUri, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = 20_000,
        }).WaitAsync(ct);

        var title = await page.TitleAsync().WaitAsync(ct);
        var statusCode = response?.Status ?? 0;

        await page.CloseAsync().WaitAsync(ct);
        return new InternalPageReport(relativePath, statusCode, title, consoleErrors);
    }

    private Uri ResolveKernelOrigin()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var raw in addresses)
            {
                var normalized = raw
                    .Replace("://0.0.0.0:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://[::]:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
                    .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal);
                if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                {
                    return new Uri(uri.GetLeftPart(UriPartial.Authority));
                }
            }
        }

        var configured = configuration[ConnectionsPublicOriginKey];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var publicUri)
            && publicUri.Scheme is "http" or "https")
        {
            return new Uri(publicUri.GetLeftPart(UriPartial.Authority));
        }

        throw new InvalidOperationException("Unable to resolve Kernel local origin for internal page inspection.");
    }
}
