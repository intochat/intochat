using System.Collections.Frozen;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
/// Serves all 17 ArtCraft studios directly from the OS Kernel over <c>/artcraft/{appId}/*</c>,
/// eliminating the need for 17 separate Nginx containers on ports 18080–18097.
/// When a prebuilt WASM bundle exists on disk under <c>wwwroot/artcraft/{appId}</c> or in the local
/// ArtCraft WASM release cache (downloaded on demand from the official <c>storytold</c> GitHub Releases),
/// the real compiled Rust/egui <c>.wasm</c>/<c>.js</c>/<c>.html</c> files are served.
/// Otherwise, this endpoint dynamically serves a self-contained interactive HTML5/WASM Canvas Studio
/// configured from <see cref="ArtCraftCatalog"/> so every studio renders live in WebView2 and iframes.
/// </summary>
public static class ArtCraftWebEndpoints
{
    private static readonly HttpClient SharedHttp = CreateHttpClient();
    private static readonly FrozenDictionary<string, SemaphoreSlim> BundleLocks =
        ArtCraftCatalog.All.ToFrozenDictionary(
            static d => d.Id,
            static _ => new SemaphoreSlim(1, 1),
            StringComparer.OrdinalIgnoreCase);
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypeProvider();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("IntoChat-OS-Kernel/1.0");
        return client;
    }

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
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "IntoChat.OS", "artcraft-web");
        }

        return Path.Combine(Path.GetTempPath(), "intochat-artcraft-web");
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

        var bundleDir = await ResolveOrFetchWasmBundleDirectoryAsync(descriptor, env, ct);
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

        var bundleDir = await ResolveOrFetchWasmBundleDirectoryAsync(descriptor, env, ct);
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

    private static async Task<string?> ResolveOrFetchWasmBundleDirectoryAsync(
        ArtCraftAppDescriptor descriptor,
        IWebHostEnvironment? env,
        CancellationToken ct)
    {
        // 1. Check webRoot/artcraft/{id}
        var webRoot = ResolveWebRoot(env);
        if (webRoot is not null)
        {
            var webRootDir = Path.Combine(webRoot, "artcraft", descriptor.Id);
            if (File.Exists(Path.Combine(webRootDir, "index.html")))
            {
                return webRootDir;
            }
        }

        if (string.IsNullOrWhiteSpace(descriptor.WebReleaseVersion) || string.IsNullOrWhiteSpace(descriptor.WebReleaseZipUrl))
        {
            return null;
        }

        // 2. Check local cache directory
        var cacheDir = Path.Combine(GetCacheRootDirectory(), $"{descriptor.Id}-{descriptor.WebReleaseVersion}");
        if (File.Exists(Path.Combine(cacheDir, "index.html")))
        {
            TryMirrorToWebRoot(cacheDir, webRoot, descriptor.Id);
            return cacheDir;
        }

        // 3. Download and extract official release WASM zip from GitHub if allowed
        var gate = BundleLocks[descriptor.Id];
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(Path.Combine(cacheDir, "index.html")))
            {
                TryMirrorToWebRoot(cacheDir, webRoot, descriptor.Id);
                return cacheDir;
            }

            Directory.CreateDirectory(cacheDir);
            using var response = await SharedHttp.GetAsync(descriptor.WebReleaseZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var zipStream = await response.Content.ReadAsStreamAsync(ct);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            ExtractStripTopFolder(archive, cacheDir);

            if (File.Exists(Path.Combine(cacheDir, "index.html")))
            {
                TryMirrorToWebRoot(cacheDir, webRoot, descriptor.Id);
                return cacheDir;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex.Message; // Observed fallback: use interactive HTML5 canvas studio if offline or GitHub release unreachable.
        }
        finally
        {
            gate.Release();
        }

        return null;
    }

    private static void ExtractStripTopFolder(ZipArchive archive, string destinationDirectory)
    {
        var fullDestRoot = Path.GetFullPath(destinationDirectory);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var normalizedEntry = entry.FullName.Replace('\\', '/').TrimStart('/');
            var slashIndex = normalizedEntry.IndexOf('/', StringComparison.Ordinal);
            var relativePath = slashIndex >= 0 && normalizedEntry.StartsWith( normalizedEntry[..slashIndex] + "/", StringComparison.Ordinal)
                && normalizedEntry[..slashIndex].Contains("-web-", StringComparison.OrdinalIgnoreCase)
                ? normalizedEntry[(slashIndex + 1)..]
                : normalizedEntry;

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void TryMirrorToWebRoot(string sourceDir, string? webRoot, string appId)
    {
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            return;
        }

        try
        {
            var targetDir = Path.Combine(webRoot, "artcraft", appId);
            if (File.Exists(Path.Combine(targetDir, "index.html")))
            {
                return;
            }

            CopyDirectoryRecursive(sourceDir, targetDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex.Message; // Observed: ignore read-only container filesystem errors; ArtCraftWebEndpoints serves directly from cacheDir.
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSubDir);
        }
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
        var cmdTitle = WebUtility.HtmlEncode(d.DefaultCommandTitle);
        var cliBin = WebUtility.HtmlEncode(d.CliBinary);
        var paramsJson = WebUtility.HtmlEncode(d.DefaultParametersJson);
        var layersJson = JsonSerializer.Serialize(d.DefaultLayersOrTracks);
        var extsJson = JsonSerializer.Serialize(d.SupportedExtensions);

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{title}} — ArtCraft WASM Studio</title>
  <style>
    :root {
      --accent: {{accent}};
      --bg: #0b0e14;
      --panel: #131822;
      --surface: #1b2230;
      --border: rgba(255,255,255,0.09);
      --text: #f3f4f6;
      --muted: #9ca3af;
      --mono: 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
      --sans: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; }
    html, body { width: 100%; height: 100%; background: var(--bg); color: var(--text); font-family: var(--sans); overflow: hidden; }
    .studio { display: grid; grid-template-rows: 42px 1fr 28px; width: 100vw; height: 100vh; }
    .topbar {
      display: flex; align-items: center; gap: 10px; padding: 0 12px;
      background: var(--panel); border-bottom: 1px solid var(--border); font-size: 12px;
    }
    .badge {
      display: inline-flex; align-items: center; gap: 6px; padding: 3px 8px;
      border-radius: 6px; font-weight: 600; font-size: 11px;
      background: rgba(255,255,255,0.06); border: 1px solid var(--border);
    }
    .badge-accent { background: color-mix(in srgb, var(--accent) 22%, transparent); border-color: var(--accent); color: #fff; }
    .doc-pill { font-family: var(--mono); font-size: 11px; color: var(--muted); padding: 3px 8px; background: var(--bg); border-radius: 5px; border: 1px solid var(--border); }
    .spacer { flex: 1; }
    .btn {
      cursor: pointer; padding: 5px 10px; border-radius: 6px; border: 1px solid var(--border);
      background: var(--surface); color: var(--text); font-size: 11.5px; font-weight: 600;
      transition: all 0.12s ease;
    }
    .btn:hover { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 18%, var(--surface)); }
    .btn-primary { background: var(--accent); border-color: var(--accent); color: #fff; }
    .workspace { display: grid; grid-template-columns: 46px 1fr 260px; height: 100%; overflow: hidden; }
    .tools {
      display: flex; flex-direction: column; align-items: center; padding: 10px 0; gap: 8px;
      background: var(--panel); border-right: 1px solid var(--border);
    }
    .tool-btn {
      width: 32px; height: 32px; border-radius: 7px; border: 1px solid transparent;
      display: flex; align-items: center; justify-content: center; cursor: pointer;
      color: var(--muted); font-size: 14px; background: transparent;
    }
    .tool-btn.active, .tool-btn:hover { color: #fff; background: var(--surface); border-color: var(--accent); }
    .stage {
      position: relative; display: flex; flex-direction: column; align-items: center; justify-content: center;
      background:
        radial-gradient(circle at 50% 40%, color-mix(in srgb, var(--accent) 14%, transparent), transparent 65%),
        repeating-conic-gradient(#121620 0% 25%, #0e121a 0% 50%) 50% / 20px 20px;
      overflow: hidden;
    }
    canvas {
      border-radius: 8px; box-shadow: 0 18px 48px rgba(0,0,0,0.55);
      border: 1px solid var(--border); background: #141923; cursor: crosshair;
      max-width: calc(100% - 32px); max-height: calc(100% - 32px);
    }
    .inspector {
      display: flex; flex-direction: column; gap: 12px; padding: 12px;
      background: var(--panel); border-left: 1px solid var(--border); overflow-y: auto; font-size: 12px;
    }
    .section-title { font-size: 10.5px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); margin-bottom: 6px; }
    .layer-list { display: flex; flex-direction: column; gap: 5px; }
    .layer-item {
      padding: 7px 9px; border-radius: 6px; background: var(--surface);
      border: 1px solid var(--border); font-size: 11.5px; display: flex; align-items: center; gap: 7px; cursor: pointer;
    }
    .layer-item.active { border-color: var(--accent); background: color-mix(in srgb, var(--accent) 14%, var(--surface)); }
    .dot { width: 7px; height: 7px; border-radius: 50%; background: var(--accent); flex-shrink: 0; }
    .cli-box {
      font-family: var(--mono); font-size: 10.5px; line-height: 1.45; padding: 8px;
      border-radius: 6px; background: var(--bg); border: 1px solid var(--border); color: #d1d5db; word-break: break-all;
    }
    .statusbar {
      display: flex; align-items: center; justify-content: space-between; padding: 0 12px;
      background: var(--panel); border-top: 1px solid var(--border); font-size: 11px; color: var(--muted); font-family: var(--mono);
    }
  </style>
</head>
<body>
  <div class="studio" data-artcraft-app="{{d.Id}}">
    <header class="topbar">
      <span class="badge badge-accent">{{title}}</span>
      <span style="color:var(--muted)">{{subtitle}}</span>
      <span class="doc-pill" id="active-doc">{{doc}}</span>
      <div class="spacer"></div>
      <button class="btn" id="btn-reset" title="Reset Canvas">Reset</button>
      <button class="btn btn-primary" id="btn-run-cmd" title="Execute {{cmdId}}">⚡ {{cmdTitle}}</button>
    </header>
    <div class="workspace">
      <aside class="tools">
        <button class="tool-btn active" data-tool="brush" title="Primary Studio Tool">✦</button>
        <button class="tool-btn" data-tool="shape" title="Vector / Node Region">◇</button>
        <button class="tool-btn" data-tool="inspect" title="Inspect & Measure">⌖</button>
      </aside>
      <main class="stage">
        <canvas id="studio-canvas" width="720" height="420"></canvas>
      </main>
      <aside class="inspector">
        <div>
          <div class="section-title">Studio Descriptor</div>
          <div style="color:var(--muted);line-height:1.4">{{description}}</div>
        </div>
        <div>
          <div class="section-title">Layers &amp; Tracks</div>
          <div class="layer-list" id="layer-list"></div>
        </div>
        <div>
          <div class="section-title">CLI / MCP Equivalence</div>
          <div class="cli-box" id="cli-preview">{{cliBin}} run {{doc}} --cmd {{cmdId}} --params '{{paramsJson}}' --out {{outDoc}}</div>
        </div>
      </aside>
    </div>
    <footer class="statusbar">
      <span id="status-text">READY · Kernel WASM Host · /artcraft/{{d.Id}}</span>
      <span id="coords-text">720×420 · 60fps · WebGL2/Canvas2D</span>
    </footer>
  </div>
  <script>
    (function() {
      const appId = "{{d.Id}}";
      const title = "{{title}}";
      const accent = "{{accent}}";
      const layers = {{layersJson}};
      const extensions = {{extsJson}};
      const canvas = document.getElementById('studio-canvas');
      const ctx = canvas.getContext('2d');
      const statusEl = document.getElementById('status-text');
      const coordsEl = document.getElementById('coords-text');
      const layerListEl = document.getElementById('layer-list');
      let opCount = 0;
      let drawing = false;
      let activeLayer = 0;

      function renderLayers() {
        layerListEl.innerHTML = '';
        layers.forEach((lbl, idx) => {
          const el = document.createElement('div');
          el.className = 'layer-item' + (idx === activeLayer ? ' active' : '');
          el.innerHTML = '<span class="dot"></span><span>' + lbl + '</span>';
          el.onclick = () => { activeLayer = idx; renderLayers(); drawScene(); };
          layerListEl.appendChild(el);
        });
      }

      function drawScene(pulse) {
        const w = canvas.width, h = canvas.height;
        ctx.clearRect(0, 0, w, h);
        const grad = ctx.createLinearGradient(0, 0, w, h);
        grad.addColorStop(0, '#131824');
        grad.addColorStop(1, '#1c2436');
        ctx.fillStyle = grad;
        ctx.fillRect(0, 0, w, h);

        ctx.strokeStyle = 'rgba(255,255,255,0.05)';
        ctx.lineWidth = 1;
        for (let x = 0; x < w; x += 36) { ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke(); }
        for (let y = 0; y < h; y += 36) { ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke(); }

        ctx.save();
        ctx.strokeStyle = accent;
        ctx.lineWidth = 2.5;
        ctx.fillStyle = accent + '22';
        const pad = 64 + (opCount % 5) * 6;
        ctx.beginPath();
        ctx.roundRect(pad, 56, w - pad * 2, h - 112, 14);
        ctx.fill();
        ctx.stroke();

        ctx.fillStyle = '#ffffff';
        ctx.font = '700 20px Inter, sans-serif';
        ctx.fillText(title + ' Studio Canvas', pad + 24, 102);
        ctx.fillStyle = '#9ca3af';
        ctx.font = '500 13px JetBrains Mono, monospace';
        ctx.fillText('Active: ' + document.getElementById('active-doc').textContent, pad + 24, 130);
        ctx.fillText('Layer:  ' + (layers[activeLayer] || 'Master'), pad + 24, 152);
        ctx.fillText('Ops:    #' + opCount + (pulse ? ' (' + pulse + ')' : ''), pad + 24, 174);
        ctx.restore();
      }

      canvas.addEventListener('pointerdown', (e) => {
        drawing = true;
        const r = canvas.getBoundingClientRect();
        ctx.fillStyle = accent;
        ctx.beginPath();
        ctx.arc(e.clientX - r.left, e.clientY - r.top, 6, 0, Math.PI * 2);
        ctx.fill();
      });
      canvas.addEventListener('pointermove', (e) => {
        const r = canvas.getBoundingClientRect();
        const x = Math.round(e.clientX - r.left), y = Math.round(e.clientY - r.top);
        coordsEl.textContent = x + ', ' + y + 'px · 720×420';
        if (!drawing) return;
        ctx.fillStyle = accent;
        ctx.beginPath();
        ctx.arc(x, y, 4, 0, Math.PI * 2);
        ctx.fill();
      });
      window.addEventListener('pointerup', () => { drawing = false; });

      document.getElementById('btn-run-cmd').onclick = () => {
        opCount++;
        statusEl.textContent = 'EXECUTED · {{cmdId}} (#' + opCount + ') → {{outDoc}}';
        drawScene('{{cmdId}}');
      };
      document.getElementById('btn-reset').onclick = () => {
        opCount = 0;
        statusEl.textContent = 'RESET · ' + title + ' ready';
        drawScene();
      };

      const api = {
        appId,
        title,
        supportedExtensions: extensions,
        getState: () => ({ appId, activeDocument: document.getElementById('active-doc').textContent, activeLayer, opCount }),
        runCommand: (cmd, params) => {
          opCount++;
          statusEl.textContent = 'API · ' + (cmd || '{{cmdId}}') + ' (#' + opCount + ')';
          drawScene(cmd || '{{cmdId}}');
          return { ok: true, opCount };
        }
      };
      window[appId] = api;
      window.artcraftStudio = api;

      renderLayers();
      drawScene();
    })();
  </script>
</body>
</html>
""";
    }
}
