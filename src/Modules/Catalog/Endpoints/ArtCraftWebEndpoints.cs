using System.Net;
using System.Text;
using System.Text.Json;
using IntoChat.OS.Modules.ArtCraft;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Serves all 17 ArtCraft studios directly from the OS Kernel over <c>/artcraft/{appId}/*</c>.
/// When a prebuilt WASM bundle exists on disk under <c>wwwroot/artcraft/{appId}</c> or in the local
/// ArtCraft WASM release cache, the compiled <c>.wasm</c>/<c>.js</c>/<c>.html</c> files are served.
/// Otherwise, serves a minimal studio host page configured from <see cref="ArtCraftCatalog"/>.
/// </summary>
internal static class ArtCraftWebEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypeProvider();

    private static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".js"] = "text/javascript; charset=utf-8";
        provider.Mappings[".mjs"] = "text/javascript; charset=utf-8";
        provider.Mappings[".html"] = "text/html; charset=utf-8";
        provider.Mappings[".css"] = "text/css; charset=utf-8";
        provider.Mappings[".svg"] = "image/svg+xml";
        provider.Mappings[".ico"] = "image/x-icon";
        provider.Mappings[".png"] = "image/png";
        provider.Mappings[".json"] = "application/json; charset=utf-8";
        return provider;
    }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/artcraft/catalog.json", static () =>
            Results.Ok(ArtCraftCatalog.All.Select(static d => new
            {
                id = d.Id,
                title = d.Title,
                subtitle = d.Subtitle,
                category = d.Category,
                accentHex = d.AccentHex,
                icon = d.Icon,
                staticWebPath = d.StaticWebPath,
                webReleaseVersion = d.WebReleaseVersion,
                webReleaseZipUrl = d.WebReleaseZipUrl,
                defaultWebPort = d.DefaultWebPort,
                defaultMcpPort = d.DefaultMcpPort,
                cliBinary = d.CliBinary,
                defaultDocument = d.DefaultDocument,
                defaultOutputDocument = d.DefaultOutputDocument,
                defaultCommandId = d.DefaultCommandId,
                defaultCommandTitle = d.DefaultCommandTitle,
                supportedExtensions = d.SupportedExtensions,
                defaultLayersOrTracks = d.DefaultLayersOrTracks,
            })))
            .WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}", static (string appId, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioPageAsync(appId, context, env, ct))
            .WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}/index.html", static (string appId, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioPageAsync(appId, context, env, ct))
            .WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}/{**assetPath}", static (string appId, string assetPath, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioAssetAsync(appId, assetPath, context, env, ct))
            .WithOrder(-100);
    }

    public static string GetCacheRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(localAppData, "IntoChat.OS", "artcraft-web")
            : Path.Combine(Path.GetTempPath(), "intochat-artcraft-web");
    }

    internal static IResult ServeStudioPage(string appId, HttpContext context, IWebHostEnvironment? env = null)
        => ServeStudioPageAsync(appId, context, env, CancellationToken.None).GetAwaiter().GetResult();

    internal static async Task<IResult> ServeStudioPageAsync(
        string appId,
        HttpContext context,
        IWebHostEnvironment? env = null,
        CancellationToken ct = default)
    {
        var normalized = ArtCraftCatalog.NormalizeAppId(appId);
        if (!ArtCraftCatalog.TryGet(normalized, out var descriptor))
        {
            return Results.NotFound(new { error = $"Unknown ArtCraft studio '{appId}'." });
        }

        var docQuery = context.Request.Query["doc"].FirstOrDefault();
        var activeDoc = !string.IsNullOrWhiteSpace(docQuery) ? docQuery! : descriptor.DefaultDocument;

        var bundleDir = ResolveWasmBundleDirectory(descriptor, env);
        if (bundleDir is not null)
        {
            var diskIndex = Path.Combine(bundleDir, "index.html");
            if (File.Exists(diskIndex))
            {
                var rawHtml = await File.ReadAllTextAsync(diskIndex, Encoding.UTF8, ct);
                var baseHref = $"/artcraft/{descriptor.Id}/";
                if (!rawHtml.Contains("<base ", StringComparison.OrdinalIgnoreCase))
                {
                    rawHtml = rawHtml.Replace("<head>", $"<head>\n  <base href=\"{baseHref}\">", StringComparison.OrdinalIgnoreCase);
                }

                if (!rawHtml.Contains("data-artcraft-vfs-bridge", StringComparison.OrdinalIgnoreCase))
                {
                    var bridgeScript = RenderVfsBridgeScript(descriptor, activeDoc);
                    rawHtml = rawHtml.Replace("</head>", $"{bridgeScript}\n</head>", StringComparison.OrdinalIgnoreCase);
                }

                return Results.Content(rawHtml, "text/html; charset=utf-8", Encoding.UTF8);
            }
        }

        var html = RenderInteractiveStudioHtml(descriptor, activeDoc);
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    internal static async Task<IResult> ServeStudioAssetAsync(
        string appId,
        string assetPath,
        HttpContext context,
        IWebHostEnvironment? env = null,
        CancellationToken ct = default)
    {
        var normalized = ArtCraftCatalog.NormalizeAppId(appId);
        if (!ArtCraftCatalog.TryGet(normalized, out var descriptor))
        {
            return Results.NotFound(new { error = $"Unknown ArtCraft studio '{appId}'." });
        }

        if (string.IsNullOrWhiteSpace(assetPath)
            || string.Equals(assetPath.Trim('/'), "index.html", StringComparison.OrdinalIgnoreCase))
        {
            return await ServeStudioPageAsync(descriptor.Id, context, env, ct);
        }

        var bundleDir = ResolveWasmBundleDirectory(descriptor, env);
        if (bundleDir is not null)
        {
            var safeRelative = assetPath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            var fullBundleRoot = Path.GetFullPath(bundleDir);
            var candidatePath = Path.GetFullPath(Path.Combine(fullBundleRoot, safeRelative));
            if (candidatePath.StartsWith(fullBundleRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidatePath))
            {
                if (!ContentTypes.TryGetContentType(candidatePath, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                return Results.File(candidatePath, contentType);
            }
        }

        return Results.NotFound();
    }

    private static string? ResolveWasmBundleDirectory(ArtCraftAppDescriptor descriptor, IWebHostEnvironment? env)
    {
        var webRoot = ResolveWebRoot(env);
        if (webRoot is not null)
        {
            var webRootDir = Path.Combine(webRoot, "artcraft", descriptor.Id);
            if (File.Exists(Path.Combine(webRootDir, "index.html")))
            {
                return webRootDir;
            }
        }

        if (!string.IsNullOrWhiteSpace(descriptor.WebReleaseVersion))
        {
            var cacheDir = Path.Combine(GetCacheRootDirectory(), $"{descriptor.Id}-{descriptor.WebReleaseVersion}");
            if (File.Exists(Path.Combine(cacheDir, "index.html")))
            {
                return cacheDir;
            }
        }

        return null;
    }

    private static string? ResolveWebRoot(IWebHostEnvironment? env)
    {
        if (env is not null)
        {
            if (!string.IsNullOrWhiteSpace(env.WebRootPath) && Directory.Exists(env.WebRootPath))
            {
                return env.WebRootPath;
            }

            if (!string.IsNullOrWhiteSpace(env.ContentRootPath))
            {
                var contentWwwRoot = Path.Combine(env.ContentRootPath, "wwwroot");
                if (Directory.Exists(contentWwwRoot))
                {
                    return contentWwwRoot;
                }
            }
        }

        var baseWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(baseWwwRoot) ? baseWwwRoot : null;
    }

    public static string RenderVfsBridgeScript(ArtCraftAppDescriptor d, string? activeDocument = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        var doc = JsonSerializer.Serialize(string.IsNullOrWhiteSpace(activeDocument) ? d.DefaultDocument : activeDocument);
        var appId = JsonSerializer.Serialize(d.Id);
        var title = JsonSerializer.Serialize(d.Title);
        var extsJson = JsonSerializer.Serialize(d.SupportedExtensions);

        return $$"""
  <script data-artcraft-vfs-bridge="1">
    (function() {
      const appId = {{appId}};
      const title = {{title}};
      const supportedExtensions = {{extsJson}};
      const params = new URLSearchParams(window.location.search);
      let activeDocument = params.get('doc') || {{doc}};
      const bridge = {
        appId,
        title,
        supportedExtensions,
        getActiveDocument: () => activeDocument,
        setActiveDocument: (nextDoc) => {
          if (typeof nextDoc === 'string' && nextDoc.trim().length > 0) {
            activeDocument = nextDoc.trim();
            window.dispatchEvent(new CustomEvent('artcraft:document-changed', { detail: { appId, activeDocument } }));
          }
          return activeDocument;
        },
        getState: () => ({ appId, title, activeDocument, wasmReady: Boolean(window.wasmBindings) }),
        runCommand: (cmd, cmdParams) => {
          window.dispatchEvent(new CustomEvent('artcraft:command', { detail: { appId, cmd, params: cmdParams, activeDocument } }));
          return { ok: true, appId, cmd, activeDocument };
        }
      };
      window[appId] = bridge;
      window.artcraftStudio = bridge;
      window.addEventListener('message', (event) => {
        const data = event && event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type === 'artcraft:open' && typeof data.document === 'string') {
          bridge.setActiveDocument(data.document);
        } else if (data.type === 'artcraft:command') {
          bridge.runCommand(data.command, data.params);
        }
      });
    })();
  </script>
""";
    }

    public static string RenderInteractiveStudioHtml(ArtCraftAppDescriptor d, string? activeDocument = null)
    {
        ArgumentNullException.ThrowIfNull(d);
        var doc = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(activeDocument) ? d.DefaultDocument : activeDocument);
        var outDoc = WebUtility.HtmlEncode(d.DefaultOutputDocument);
        var title = WebUtility.HtmlEncode(d.Title);
        var subtitle = WebUtility.HtmlEncode(d.Subtitle);
        var description = WebUtility.HtmlEncode(d.Description);
        var accent = WebUtility.HtmlEncode(d.AccentHex);
        var cmdId = WebUtility.HtmlEncode(d.DefaultCommandId);
        var cliBin = WebUtility.HtmlEncode(d.CliBinary);
        var paramsJson = WebUtility.HtmlEncode(d.DefaultParametersJson);
        var bridgeScript = RenderVfsBridgeScript(d, activeDocument);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{title}} — ArtCraft Studio</title>
{{bridgeScript}}
</head>
<body style="margin:0;background:#0b0e14;color:#f3f4f6;font-family:system-ui,sans-serif">
  <main data-artcraft-app="{{d.Id}}" style="padding:16px">
    <header style="border-left:3px solid {{accent}};padding-left:10px;margin-bottom:12px">
      <strong>{{title}}</strong> · <span>{{subtitle}}</span>
      <div id="active-doc" style="font-family:monospace;font-size:12px;color:#9ca3af">{{doc}}</div>
    </header>
    <p style="font-size:13px;color:#9ca3af;margin-bottom:12px">{{description}}</p>
    <pre style="padding:10px;background:#131822;border-radius:6px;font-size:11px;overflow:auto">{{cliBin}} run {{doc}} --cmd {{cmdId}} --params '{{paramsJson}}' --out {{outDoc}}</pre>
  </main>
</body>
</html>
""";
    }
}
