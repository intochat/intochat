using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Serves the free open-source IntoChat.OS Widgetbook web build at <c>/widgetbook/*</c>
/// when present under <c>wwwroot/widgetbook</c> or <c>ui/build/web-widgetbook</c>.
/// </summary>
internal static class WidgetbookWebEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/widgetbook", static (IWebHostEnvironment env) => ServeIndex(env))
            .WithOrder(-100);
        endpoints.MapGet("/widgetbook/index.html", static (IWebHostEnvironment env) => ServeIndex(env))
            .WithOrder(-100);
        endpoints.MapGet("/widgetbook/{**assetPath}", static (string assetPath, IWebHostEnvironment env) => ServeAsset(assetPath, env))
            .WithOrder(-100);
    }

    private static IResult ServeIndex(IWebHostEnvironment env)
    {
        var dir = ResolveWidgetbookDirectory(env);
        if (dir is not null)
        {
            var indexPath = Path.Combine(dir, "index.html");
            if (File.Exists(indexPath))
            {
                var html = File.ReadAllText(indexPath, Encoding.UTF8);
                if (!html.Contains("<base ", StringComparison.OrdinalIgnoreCase))
                {
                    html = html.Replace("<head>", "<head>\n  <base href=\"/widgetbook/\">", StringComparison.OrdinalIgnoreCase);
                }
                return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
            }
        }

        return Results.Content(RenderSetupPlaceholderHtml(), "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static IResult ServeAsset(string assetPath, IWebHostEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(assetPath)
            || string.Equals(assetPath.Trim('/'), "index.html", StringComparison.OrdinalIgnoreCase))
        {
            return ServeIndex(env);
        }

        var dir = ResolveWidgetbookDirectory(env);
        if (dir is null)
        {
            return Results.NotFound(new { error = "Widgetbook web bundle is not built yet." });
        }

        var safeRelative = assetPath.Replace('\\', '/').TrimStart('/');
        if (safeRelative.Contains("..", StringComparison.Ordinal))
        {
            return Results.BadRequest();
        }

        var fullPath = Path.GetFullPath(Path.Combine(dir, safeRelative));
        var rootFull = Path.GetFullPath(dir);
        if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        var contentType = ContentTypes.TryGetContentType(fullPath, out var detected)
            ? detected
            : "application/octet-stream";
        return Results.File(fullPath, contentType);
    }

    internal static string? ResolveWidgetbookDirectory(IWebHostEnvironment? env)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(env?.WebRootPath))
        {
            candidates.Add(Path.Combine(env.WebRootPath, "widgetbook"));
        }
        if (!string.IsNullOrWhiteSpace(env?.ContentRootPath))
        {
            candidates.Add(Path.Combine(env.ContentRootPath, "wwwroot", "widgetbook"));
            candidates.Add(Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "ui", "build", "web-widgetbook")));
        }

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RenderSetupPlaceholderHtml() => """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>IntoChat.OS Widgetbook</title>
          <style>
            body { margin: 0; font-family: Inter, system-ui, sans-serif; background: #0b0e14; color: #e6ecf5; display: grid; place-items: center; min-height: 100vh; }
            .card { max-width: 640px; padding: 32px; background: #161c2a; border: 1px solid #242e42; border-radius: 14px; }
            h1 { margin: 0 0 10px; font-size: 20px; }
            p { color: #9aa7bd; line-height: 1.6; font-size: 14px; }
            pre { background: #0e131d; border: 1px solid #242e42; padding: 14px; border-radius: 8px; overflow-x: auto; font-size: 12.5px; color: #60a5fa; }
          </style>
        </head>
        <body>
          <div class="card">
            <h1>IntoChat.OS Widgetbook (Open Source)</h1>
            <p>Build the static Widgetbook bundle once to serve it directly from the OS Kernel at <code>/widgetbook</code>, or switch the toolbar to <strong>Dev Hot-Reload</strong>.</p>
            <pre>cd ui
        flutter build web -t lib/widgetbook.dart --base-href /widgetbook/ -o build/web-widgetbook</pre>
            <p>For live hot-reload while extending the UI kit:</p>
            <pre>cd ui
        flutter run -d web-server --web-port 8090 -t lib/widgetbook.dart</pre>
          </div>
        </body>
        </html>
        """;
}
