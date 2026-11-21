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
            .AllowAnonymous()
            .WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}", static (string appId, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioPageAsync(appId, context, env, ct))
            .AllowAnonymous()
            .WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}/index.html", static (string appId, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioPageAsync(appId, context, env, ct))
            .AllowAnonymous()
            .WithOrder(-100);

        endpoints.MapPost("/artcraft/{appId}/install", static async (string appId, HttpContext context, CancellationToken ct) =>
        {
            if (context.Connection.RemoteIpAddress is { } remoteIp && !IPAddress.IsLoopback(remoteIp))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var normalized = ArtCraftCatalog.NormalizeAppId(appId);
            if (!ArtCraftCatalog.TryGet(normalized, out var descriptor))
            {
                return Results.NotFound(new { error = $"Unknown ArtCraft studio '{appId}'." });
            }

            var result = await ArtCraftWasmBundleManager.EnsureInstalledAsync(descriptor, ct: ct).ConfigureAwait(false);
            return result.Installed
                ? Results.Ok(new { installed = true, appId = descriptor.Id, path = descriptor.StaticWebPath })
                : Results.Problem(detail: result.Error, statusCode: StatusCodes.Status502BadGateway);
        }).AllowAnonymous().WithOrder(-100);

        endpoints.MapGet("/artcraft/{appId}/{**assetPath}", static (string appId, string assetPath, HttpContext context, IWebHostEnvironment env, CancellationToken ct) =>
            ServeStudioAssetAsync(appId, assetPath, context, env, ct))
            .AllowAnonymous()
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
        loadArtifactBytes: async (docPath) => {
          const target = docPath || activeDocument;
          const res = await fetch('/artifacts/bytes?path=' + encodeURIComponent(target));
          if (!res.ok) return null;
          return new Uint8Array(await res.arrayBuffer());
        },
        saveArtifactBytes: async (bytes, docPath) => {
          const target = docPath || activeDocument;
          const res = await fetch('/artifacts/bytes?path=' + encodeURIComponent(target) + '&actor=user', {
            method: 'PUT',
            body: bytes
          });
          return res.ok ? await res.json() : null;
        },
        runCommand: (cmd, cmdParams) => {
          window.dispatchEvent(new CustomEvent('artcraft:command', { detail: { appId, cmd, params: cmdParams, activeDocument } }));
          return { ok: true, appId, cmd, activeDocument };
        }
      };
      // Live SSE subscription: whenever Ino mutates activeDocument via MCP, notify the open WASM canvas immediately.
      try {
        const es = new EventSource('/artifacts/stream?path=' + encodeURIComponent(activeDocument));
        es.addEventListener('artifact', (msg) => {
          try {
            const detail = JSON.parse(msg.data);
            window.dispatchEvent(new CustomEvent('artcraft:artifact-updated', { detail }));
          } catch (_) {}
        });
      } catch (_) {}
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
        if (string.Equals(d.Id, "artcraft", StringComparison.OrdinalIgnoreCase))
        {
            return RenderArtCraftHubHtml(d, activeDocument);
        }

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
        var hasWasmRelease = !string.IsNullOrWhiteSpace(d.WebReleaseZipUrl);
        var autoBootFlag = hasWasmRelease ? "true" : "false";

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{title}} — ArtCraft Studio</title>
{{bridgeScript}}
  <style>
    @keyframes spin { to { transform: rotate(360deg); } }
    .spinner { width: 16px; height: 16px; border: 2px solid rgba(255,255,255,0.2); border-top-color: {{accent}}; border-radius: 50%; animation: spin 0.8s linear infinite; display: inline-block; }
  </style>
</head>
<body style="margin:0;background:#0b0e14;color:#f3f4f6;font-family:Inter,system-ui,sans-serif;min-height:100vh;display:grid;place-items:center">
  <main data-artcraft-app="{{d.Id}}" style="max-width:640px;width:90%;padding:28px;background:#161c2a;border:1px solid #242e42;border-radius:14px">
    <header style="border-left:3px solid {{accent}};padding-left:12px;margin-bottom:14px">
      <strong style="font-size:18px">{{title}}</strong> · <span style="color:#9aa7bd">{{subtitle}}</span>
      <div id="active-doc" style="font-family:monospace;font-size:12px;color:#9ca3af;margin-top:4px">{{doc}}</div>
    </header>
    <p style="font-size:13.5px;color:#9aa7bd;margin-bottom:16px;line-height:1.55">{{description}}</p>
    <div style="display:flex;gap:10px;align-items:center;margin-bottom:16px">
      <button id="install-wasm-btn" style="background:{{accent}};color:#0b0e14;border:none;border-radius:8px;padding:10px 16px;font-weight:700;cursor:pointer">Download &amp; Launch WASM Studio</button>
      <span id="install-spinner" class="spinner" style="display:none"></span>
      <span id="install-status" style="font-size:12.5px;color:#9aa7bd"></span>
    </div>
    <pre style="padding:10px;background:#0e131d;border:1px solid #242e42;border-radius:8px;font-size:11px;overflow:auto;color:#60a5fa">{{cliBin}} run {{doc}} --cmd {{cmdId}} --params '{{paramsJson}}' --out {{outDoc}}</pre>
  </main>
  <script>
    (function() {
      const btn = document.getElementById('install-wasm-btn');
      const status = document.getElementById('install-status');
      const spinner = document.getElementById('install-spinner');
      const autoBoot = {{autoBootFlag}};
      if (!btn) return;
      async function installAndLaunch() {
        btn.disabled = true;
        if (spinner) spinner.style.display = 'inline-block';
        status.textContent = 'Downloading {{title}} WASM bundle...';
        try {
          const res = await fetch('/artcraft/{{d.Id}}/install', { method: 'POST' });
          if (res.ok) {
            status.textContent = 'Installed! Launching {{title}}...';
            window.location.reload();
          } else {
            const body = await res.json().catch(() => ({}));
            status.textContent = body.detail || ('Install failed (HTTP ' + res.status + ')');
            if (spinner) spinner.style.display = 'none';
            btn.disabled = false;
          }
        } catch (err) {
          status.textContent = 'Install error: ' + err;
          if (spinner) spinner.style.display = 'none';
          btn.disabled = false;
        }
      }
      btn.addEventListener('click', installAndLaunch);
      if (autoBoot) {
        installAndLaunch();
      }
    })();
  </script>
</body>
</html>
""";
    }

    private static string RenderArtCraftHubHtml(ArtCraftAppDescriptor hub, string? activeDocument)
    {
        var bridgeScript = RenderVfsBridgeScript(hub, activeDocument);
        var cards = new StringBuilder();
        foreach (var app in ArtCraftCatalog.All)
        {
            if (string.Equals(app.Id, "artcraft", StringComparison.OrdinalIgnoreCase)) { continue; }
            var title = WebUtility.HtmlEncode(app.Title);
            var sub = WebUtility.HtmlEncode(app.Subtitle);
            var accent = WebUtility.HtmlEncode(app.AccentHex);
            var exts = WebUtility.HtmlEncode(string.Join(" ", app.SupportedExtensions.Take(4)));
            var badge = !string.IsNullOrWhiteSpace(app.WebReleaseVersion)
                ? $"WASM v{WebUtility.HtmlEncode(app.WebReleaseVersion)}"
                : "MCP / CLI";
            cards.Append($$"""
              <a href="/artcraft/{{app.Id}}/index.html" style="text-decoration:none;color:inherit;background:#161c2a;border:1px solid #242e42;border-top:3px solid {{accent}};border-radius:12px;padding:16px;display:flex;flex-direction:column;gap:6px;transition:transform 0.15s ease,border-color 0.15s ease">
                <div style="display:flex;justify-content:space-between;align-items:center">
                  <strong style="font-size:15px">{{title}}</strong>
                  <span style="font-size:11px;padding:2px 8px;border-radius:999px;background:rgba(255,255,255,0.06);color:{{accent}};font-weight:600">{{badge}}</span>
                </div>
                <div style="font-size:12.5px;color:#9aa7bd">{{sub}}</div>
                <div style="font-family:monospace;font-size:11px;color:#6f7d95;margin-top:auto">{{exts}}</div>
              </a>
            """);
        }

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>ArtCraft — Creative &amp; Productivity Suite</title>
{{bridgeScript}}
</head>
<body data-artcraft-app="{{hub.Id}}" style="margin:0;background:#0b0e14;color:#e6ecf5;font-family:Inter,system-ui,sans-serif;min-height:100vh;padding:28px 32px;box-sizing:border-box">
  <header style="margin-bottom:22px">
    <h1 style="margin:0 0 6px;font-size:22px;letter-spacing:-0.02em">ArtCraft Suite</h1>
    <p style="margin:0;font-size:13.5px;color:#9aa7bd">Select any studio to launch its fullscreen Rust WASM workspace (downloads automatically on first open).</p>
  </header>
  <section style="display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px">
    {{cards}}
  </section>
</body>
</html>
""";
    }
}
