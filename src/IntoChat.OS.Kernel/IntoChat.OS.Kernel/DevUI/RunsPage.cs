using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Kernel.DevUI;

// Next to DevUI in Development: /runs/ lists everything running in the caller's brain (flows, agent turns, operations) from the runs
// index, refreshed every two seconds, and /runs.json gives the same list. One run opens by id at /runs.json?id=...
internal static class RunsPage
{
    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/runs.json", async (string? id, int? limit, IServiceProvider services) =>
        {
            var runs = SiloNeurons.Get<IRuns>(services, IRuns.Key(((services.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current ?? throw new UnauthorizedAccessException("The request carries no caller.")).Brain.Value));
            return id is { Length: > 0 }
                ? (await runs.Read(id) is { } run ? Results.Json(run) : Results.NotFound())
                : Results.Json(await runs.List(limit ?? 100));
        });
        app.MapGet("/runs/", () => Results.Content(Html, "text/html"));
    }

    private const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Runs</title><style>
        :root{color-scheme:light dark;--bg:#fff;--fg:#1b1b1f;--muted:#6b6b76;--line:#e4e4e9;--run:#2563eb;--wait:#b45309;--ok:#15803d;--bad:#b91c1c}
        @media (prefers-color-scheme:dark){:root{--bg:#111114;--fg:#ececf1;--muted:#9a9aa5;--line:#2a2a31;--run:#60a5fa;--wait:#fbbf24;--ok:#4ade80;--bad:#f87171}}
        body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:14px/1.4 system-ui,sans-serif}
        h1{font-size:18px;margin:0 0 12px}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}
        th{color:var(--muted);font-weight:500}.Running{color:var(--run)}.Waiting{color:var(--wait)}.Succeeded{color:var(--ok)}.Failed,.Cancelled{color:var(--bad)}
        code{font-size:12px;color:var(--muted)}td.title{max-width:40ch;overflow-wrap:anywhere}</style></head>
        <body><h1>Runs</h1><table><thead><tr><th>State</th><th>Kind</th><th>Title</th><th>Waiting / detail</th><th>Started</th><th>Id</th></tr></thead>
        <tbody id="rows"></tbody></table><script>
        const esc=s=>String(s??"").replace(/[&<>"]/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
        const kinds=["Flow","Agent turn","Operation","Workflow","Code block"],states=["Running","Waiting","Succeeded","Failed","Cancelled"];
        async function load(){try{const r=await fetch("/runs.json");const runs=await r.json();
        document.getElementById("rows").innerHTML=runs.map(x=>{const s=states[x.state]??x.state;return `<tr><td class="${s}">${s}</td><td>${kinds[x.kind]??x.kind}</td><td class="title">${esc(x.title)}</td><td>${esc(x.waiting??x.detail??x.current??"")}</td><td>${new Date(x.started).toLocaleTimeString()}</td><td><code>${esc(x.id)}</code></td></tr>`}).join("")}catch(e){}}
        load();setInterval(load,2000);</script></body></html>
        """;
}
