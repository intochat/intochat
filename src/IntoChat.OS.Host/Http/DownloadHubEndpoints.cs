using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Host.Http;

public static class DownloadHubEndpoints
{
    public const string Path = "/download";
    public const string PluralPath = "/downloads";
    public const string ReleaseDownloadBase = "https://github.com/intochat/intochat/releases/latest/download";

    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="color-scheme" content="light dark">
<meta name="theme-color" content="#FBFAF7" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#17140F" media="(prefers-color-scheme: dark)">
<title>Download IntoChat OS</title>
<style>
:root{--bg:#FBFAF7;--sunk:#F2EFE9;--card:#FFFFFF;--ink:#17140F;--body:#2A251E;--muted:#7C7264;--border:#E4DFD5;--accent:#1F5EFF;--accent-h:#1541C7;--accent-bg:#E7EEFF;--done:#12A150;--done-bg:#E1F6EA;--warn:#E8A317;--warn-bg:#FCF1DA;--sh:0 1px 2px rgba(23,20,15,.05),0 8px 24px rgba(23,20,15,.05)}
@media(prefers-color-scheme:dark){:root{--bg:#17140F;--sunk:#110F0B;--card:#2A251E;--ink:#FBFAF7;--body:#F0ECE4;--muted:#9C9182;--border:#423B31;--accent:#4B7DFF;--accent-h:#1F5EFF;--accent-bg:rgba(75,125,255,.16);--done:#8FE0AF;--done-bg:rgba(18,161,80,.16);--warn:#F6D28A;--warn-bg:rgba(232,163,23,.16);--sh:0 2px 12px rgba(0,0,0,.35)}}
*{box-sizing:border-box}
html,body{margin:0;background:var(--bg);color:var(--body);font-family:'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;line-height:1.5;-webkit-font-smoothing:antialiased}
.wrap{max-width:1040px;margin:0 auto;padding:28px 20px 56px}
.top{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:28px;flex-wrap:wrap}
.brand{display:flex;align-items:center;gap:10px;color:var(--ink);font-weight:700;font-size:16px;text-decoration:none}
.mark{width:30px;height:30px;border-radius:9px;background:var(--accent);color:#fff;display:grid;place-items:center;font-weight:800;font-size:14px}
.hero{background:var(--card);border:1px solid var(--border);border-radius:20px;padding:28px;box-shadow:var(--sh);margin-bottom:24px;display:flex;flex-wrap:wrap;align-items:center;justify-content:space-between;gap:20px}
.hero h1{margin:0 0 8px;font-size:clamp(24px,4vw,34px);line-height:1.15;color:var(--ink);letter-spacing:-.02em}
.hero p{margin:0;color:var(--muted);max-width:560px;font-size:15px}
.pill{display:inline-flex;align-items:center;gap:6px;padding:4px 10px;border-radius:999px;font-size:12px;font-weight:600;background:var(--accent-bg);color:var(--accent);margin-bottom:10px}
.cta{display:inline-flex;align-items:center;gap:8px;padding:12px 18px;border-radius:12px;background:var(--accent);color:#fff;text-decoration:none;font-weight:600;font-size:14px;white-space:nowrap;transition:background .12s ease}
.cta:hover{background:var(--accent-h)}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(285px,1fr));gap:16px}
.card{background:var(--card);border:1px solid var(--border);border-radius:16px;padding:20px;box-shadow:var(--sh);display:flex;flex-direction:column;gap:14px;transition:border-color .15s ease,transform .15s ease}
.card.detected{border-color:var(--accent);box-shadow:0 0 0 2px var(--accent-bg),var(--sh);order:-1}
.chead{display:flex;align-items:center;justify-content:space-between;gap:8px}
.ptitle{display:flex;align-items:center;gap:10px}
.pico{width:34px;height:34px;border-radius:10px;background:var(--sunk);color:var(--ink);display:grid;place-items:center;flex:none}
.card.detected .pico{background:var(--accent-bg);color:var(--accent)}
.card h2{margin:0;font-size:16px;color:var(--ink)}
.sub{font-size:12px;color:var(--muted)}
.badge{display:none;padding:3px 8px;border-radius:999px;font-size:11px;font-weight:700;background:var(--done-bg);color:var(--done)}
.card.detected .badge{display:inline-flex}
.links{display:flex;flex-direction:column;gap:8px}
.dl{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:10px 12px;border-radius:10px;border:1px solid var(--border);background:var(--bg);color:var(--ink);text-decoration:none;font-size:13px;font-weight:600;transition:border-color .12s ease,background .12s ease}
.dl:hover{border-color:var(--accent);background:var(--accent-bg)}
.dl.primary{background:var(--ink);color:var(--bg);border-color:var(--ink)}
.dl.primary:hover{opacity:.9}
.dl span:last-child{font-size:11px;font-weight:500;opacity:.75}
.cmd{background:var(--sunk);border:1px solid var(--border);border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:6px}
.cmdtop{display:flex;align-items:center;justify-content:space-between;font-size:11px;font-weight:600;color:var(--muted)}
.cmd code{font-family:inherit;font-variant-numeric:tabular-nums;font-size:12px;color:var(--ink);word-break:break-all;user-select:all}
.copy{border:1px solid var(--border);background:var(--card);color:var(--ink);border-radius:7px;padding:3px 8px;font-size:11px;font-weight:600;cursor:pointer;font-family:inherit}
.copy:hover{border-color:var(--accent);color:var(--accent)}
.pwa{background:var(--warn-bg);border:1px solid var(--border);border-radius:10px;padding:11px 12px;font-size:12.5px;color:var(--body)}
.pwa strong{display:block;color:var(--ink);margin-bottom:4px}
.pwa ol{margin:0;padding-left:18px}
footer{margin-top:28px;display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap;font-size:12.5px;color:var(--muted)}
footer a{color:var(--accent);text-decoration:none}
footer a:hover{text-decoration:underline}
</style>
</head>
<body>
<main class="wrap">
<header class="top">
<a class="brand" href="/"><span class="mark">IC</span><span>IntoChat OS</span></a>
<a class="cta" href="/">🚀 Open Live IntoChat OS in Browser →</a>
</header>

<section class="hero">
<div>
<div class="pill" id="os-pill">⚡ Instant Download Hub · All 5 Platforms</div>
<h1>Run IntoChat OS natively on any device</h1>
<p>Install the native desktop or mobile app from official GitHub Releases, or launch your live personal AI OS immediately in the browser with zero setup.</p>
</div>
<a class="cta" href="/">🚀 Open Live IntoChat OS in Browser →</a>
</section>

<section class="grid" aria-label="Platform downloads">
<article class="card" data-os="windows">
<div class="chead">
<div class="ptitle">
<span class="pico" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M3 5.5 11 4.4v7.1H3zm9-1.2L21 3v8.5h-9zM3 12.5h8v7.1l-8-1.1zm9 0h9V21l-9-1.3z"/></svg></span>
<div><h2>Windows</h2><div class="sub">Windows 10/11 · x64</div></div>
</div>
<span class="badge">Detected</span>
</div>
<div class="links">
<a class="dl primary" href="https://github.com/intochat/intochat/releases/latest/download/IntoChat-Setup-win-x64.exe"><span>IntoChat-Setup-win-x64.exe</span><span>Installer</span></a>
<a class="dl" href="https://github.com/intochat/intochat/releases/latest/download/intochat-win-x64.zip"><span>intochat-win-x64.zip</span><span>Portable ZIP</span></a>
</div>
<div class="cmd">
<div class="cmdtop"><span>1-line PowerShell</span><button class="copy" type="button" data-copy="irm https://github.com/intochat/intochat/releases/latest/download/install.ps1 | iex">Copy</button></div>
<code>irm https://github.com/intochat/intochat/releases/latest/download/install.ps1 | iex</code>
</div>
</article>

<article class="card" data-os="macos">
<div class="chead">
<div class="ptitle">
<span class="pico" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor"><path d="M16.4 12.7c0-2.1 1.7-3.1 1.8-3.2-1-1.5-2.5-1.7-3-1.7-1.3-.1-2.5.8-3.1.8-.6 0-1.6-.8-2.7-.7-1.4 0-2.7.8-3.4 2.1-1.5 2.5-.4 6.2 1 8.2.7 1 1.5 2.1 2.6 2 1 0 1.4-.7 2.7-.7s1.6.7 2.7.7c1.1 0 1.8-1 2.5-2 .8-1.1 1.1-2.2 1.1-2.3-.1 0-2.2-.8-2.2-3.2zM14.3 6.3c.6-.7 1-1.7.9-2.7-.9 0-1.9.6-2.5 1.3-.5.6-1 1.6-.9 2.6 1 .1 2-.5 2.5-1.2z"/></svg></span>
<div><h2>macOS</h2><div class="sub">Apple Silicon · arm64</div></div>
</div>
<span class="badge">Detected</span>
</div>
<div class="links">
<a class="dl primary" href="https://github.com/intochat/intochat/releases/latest/download/IntoChat-macos-arm64.dmg"><span>IntoChat-macos-arm64.dmg</span><span>Apple Silicon DMG</span></a>
<a class="dl" href="https://github.com/intochat/intochat/releases/latest/download/intochat-osx-arm64.tar.gz"><span>intochat-osx-arm64.tar.gz</span><span>Archive</span></a>
</div>
<div class="cmd">
<div class="cmdtop"><span>1-line Terminal</span><button class="copy" type="button" data-copy="curl -fsSL https://github.com/intochat/intochat/releases/latest/download/install.sh | sh">Copy</button></div>
<code>curl -fsSL https://github.com/intochat/intochat/releases/latest/download/install.sh | sh</code>
</div>
</article>

<article class="card" data-os="linux">
<div class="chead">
<div class="ptitle">
<span class="pico" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="4 17 10 11 4 5"/><line x1="12" y1="19" x2="20" y2="19"/></svg></span>
<div><h2>Linux</h2><div class="sub">Desktop &amp; Server · x64</div></div>
</div>
<span class="badge">Detected</span>
</div>
<div class="links">
<a class="dl primary" href="https://github.com/intochat/intochat/releases/latest/download/IntoChat-linux-x86_64.AppImage"><span>IntoChat-linux-x86_64.AppImage</span><span>AppImage</span></a>
<a class="dl" href="https://github.com/intochat/intochat/releases/latest/download/intochat-linux-amd64.deb"><span>intochat-linux-amd64.deb</span><span>Debian / Ubuntu</span></a>
<a class="dl" href="https://github.com/intochat/intochat/releases/latest/download/intochat-linux-x64.tar.gz"><span>intochat-linux-x64.tar.gz</span><span>Tarball</span></a>
</div>
</article>

<article class="card" data-os="android">
<div class="chead">
<div class="ptitle">
<span class="pico" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="5" y="2" width="14" height="20" rx="2"/><line x1="12" y1="18" x2="12.01" y2="18"/></svg></span>
<div><h2>Android</h2><div class="sub">Direct APK &amp; Chrome PWA</div></div>
</div>
<span class="badge">Detected</span>
</div>
<div class="links">
<a class="dl primary" href="https://github.com/intochat/intochat/releases/latest/download/intochat-android.apk"><span>intochat-android.apk</span><span>Android APK</span></a>
</div>
<div class="pwa">
<strong>Instant Chrome PWA</strong>
Open <a href="/">IntoChat OS</a> in Chrome and tap <em>Install app</em> or <em>Add to Home screen</em> for a fullscreen native feel.
</div>
</article>

<article class="card" data-os="ios">
<div class="chead">
<div class="ptitle">
<span class="pico" aria-hidden="true"><svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="6" y="2" width="12" height="20" rx="3"/><line x1="10" y1="5" x2="14" y2="5"/></svg></span>
<div><h2>iOS</h2><div class="sub">2-Tap PWA &amp; Unsigned IPA</div></div>
</div>
<span class="badge">Detected</span>
</div>
<div class="pwa">
<strong>2-Tap Add to Home Screen (PWA)</strong>
<ol>
<li>Open <a href="/">IntoChat OS</a> in Safari and tap <b>Share</b> (↑).</li>
<li>Tap <b>Add to Home Screen</b> — launches fullscreen with zero App Store friction.</li>
</ol>
</div>
<div class="links">
<a class="dl" href="https://github.com/intochat/intochat/releases/latest/download/intochat-ios-unsigned.ipa"><span>intochat-ios-unsigned.ipa</span><span>Unsigned IPA</span></a>
</div>
</article>
</section>

<footer>
<span>Served directly by IntoChat OS · Zero external CDN dependencies</span>
<a href="https://github.com/intochat/intochat/releases/latest">All GitHub Releases →</a>
</footer>
</main>
<script>
(function(){
var ua=navigator.userAgent||'';
var os=null,label='';
if(/iPhone|iPad|iPod/i.test(ua)||(/Macintosh/i.test(ua)&&navigator.maxTouchPoints>1)){os='ios';label='iOS';}
else if(/Android/i.test(ua)){os='android';label='Android';}
else if(/Windows|Win32|Win64|WOW64/i.test(ua)){os='windows';label='Windows';}
else if(/Macintosh|Mac OS X|MacIntel/i.test(ua)){os='macos';label='macOS';}
else if(/Linux|X11|CrOS/i.test(ua)){os='linux';label='Linux';}
if(os){
var card=document.querySelector('[data-os="'+os+'"]');
if(card){card.classList.add('detected');}
var pill=document.getElementById('os-pill');
if(pill){pill.textContent='⚡ Detected '+label+' · Recommended build highlighted below';}
}
document.querySelectorAll('[data-copy]').forEach(function(btn){
btn.addEventListener('click',function(){
var text=btn.getAttribute('data-copy');
if(!text)return;
var done=function(){btn.textContent='Copied';setTimeout(function(){btn.textContent='Copy';},1600);};
if(navigator.clipboard&&navigator.clipboard.writeText){navigator.clipboard.writeText(text).then(done);}
});
});
})();
</script>
</body>
</html>
""";

    private static readonly byte[] HtmlBytes = Encoding.UTF8.GetBytes(Html);

    public static int PayloadByteLength => HtmlBytes.Length;

    public static IEndpointRouteBuilder MapDownloadHub(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapMethods(Path, [HttpMethods.Get, HttpMethods.Head], WriteAsync).AllowAnonymous();
        endpoints.MapMethods(PluralPath, [HttpMethods.Get, HttpMethods.Head], WriteAsync).AllowAnonymous();
        return endpoints;
    }

    private static Task WriteAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = HtmlBytes.Length;
        context.Response.Headers.CacheControl = "public, max-age=300";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return Task.CompletedTask;
        }

        return context.Response.Body.WriteAsync(HtmlBytes, context.RequestAborted).AsTask();
    }
}
