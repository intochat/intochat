using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Debugger;
using IntoChat.OS.Modules.Apps.Runs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Kernel.DevUI;

// The brain in 3D, next to DevUI in Development: /brain/ draws the caller's neurons (by role) and the wires between them, and makes
// a neuron pulse while one of its runs is running or waiting. /brain.json is the same data: topology from the debugger reads,
// live runs from the runs index.
internal static class BrainPage
{
    internal sealed record BrainNode(string Id, string Label, string Role, string? Run, string? Waiting);

    internal sealed record BrainWire(string From, string To, string Signal);

    internal sealed record BrainScene(BrainNode[] Nodes, BrainWire[] Wires);

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/brain.json", async (IServiceProvider services) =>
        {
            var brain = ((services.GetService(typeof(DigitalBrain.Access.ICallerAmbient)) as DigitalBrain.Access.ICallerAmbient)?.Current ?? throw new UnauthorizedAccessException("The request carries no caller.")).Brain.Value;
            var topology = await SiloNeurons.Get<IDebuggerReads>(services, new DigitalBrain.NeuronId(new DigitalBrain.BrainId(brain), "creator.reads", IDebuggerReads.DefaultKey)).Topology();
            var wires = services.GetService(typeof(DigitalBrain.Kernel.IBrainObserver)) is DigitalBrain.Kernel.IBrainObserver observer ? await observer.Wires() : [];
            var runs = await SiloNeurons.Get<IRuns>(services, IRuns.Key(brain)).List(200);
            return Results.Json(Scene(topology, wires, runs));
        });
        app.MapGet("/brain/", () => Results.Content(Html, "text/html"));
    }

    // Neurons are the topology's plus every wire's ends; wires are the brain's durable wires. A neuron is live while a run it owns, or a
    // run of the synapse it hosts, is running or waiting; a live run no neuron matches is drawn as its own node.
    internal static BrainScene Scene(BrainTopology topology, DigitalBrain.Kernel.ObservedWire[] wires, RunView[] runs)
    {
        var live = runs.Where(run => run.State is RunState.Running or RunState.Waiting).ToList();
        var known = topology.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var end in wires.SelectMany(wire => new[] { wire.From, wire.To }).Where(end => !known.ContainsKey(end)))
        {
            known[end] = new BrainTopologyNode(end, Label(end), Role(end));
        }
        var nodes = new List<BrainNode>();
        foreach (var node in known.Values)
        {
            var run = live.FirstOrDefault(item => Matches(node, item));
            if (run is not null) { live.Remove(run); }
            nodes.Add(new(node.Id, node.Label, node.Role, run?.Title, run?.Waiting));
        }
        nodes.AddRange(live.Select(run => new BrainNode($"run:{run.Id}", run.Title, run.Kind.ToString(), run.Title, run.Waiting)));
        return new([.. nodes], [.. wires.Select(wire => new BrainWire(wire.From, wire.To, wire.Signal))]);
    }

    // "brain/type/key": the type is the role a wire end plays, the key its label.
    private static string Role(string id) => id.Split('/') is { Length: > 2 } parts ? parts[1] : "neuron";

    private static string Label(string id) => id.Split('/') is { Length: > 2 } parts ? string.Join('/', parts[2..]) : id;

    private static bool Matches(BrainTopologyNode node, RunView run)
        => node.Id.EndsWith("/" + run.Id, StringComparison.Ordinal)
            || node.Id.EndsWith(run.Owner, StringComparison.Ordinal) && run.Owner.Length > 0
            || run.Synapse is { Length: > 0 } synapse && (node.Label == synapse || node.Id.EndsWith("/" + synapse, StringComparison.Ordinal));

    private const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Brain</title><style>
        :root{color-scheme:light dark;--bg:#fafafa;--fg:#1b1b1f;--muted:#6b6b76;--wire:rgba(80,80,100,.25)}
        @media (prefers-color-scheme:dark){:root{--bg:#0d0d10;--fg:#ececf1;--muted:#9a9aa5;--wire:rgba(170,170,200,.22)}}
        html,body{margin:0;height:100%;background:var(--bg);color:var(--fg);font:13px/1.4 system-ui,sans-serif}
        canvas{display:block;width:100%;height:100%;touch-action:none}#info{position:fixed;left:16px;top:12px;right:16px;pointer-events:none}
        #info b{font-size:16px}#legend span{margin-right:12px;white-space:nowrap}#tip{position:fixed;pointer-events:none;padding:4px 8px;border-radius:6px;background:var(--bg);border:1px solid var(--wire);display:none;max-width:320px}
        </style></head><body><canvas id="c"></canvas>
        <div id="info"><b>Brain</b> <span id="count" style="color:var(--muted)"></span><div id="legend"></div></div><div id="tip"></div><script>
        const c=document.getElementById("c"),x=c.getContext("2d"),tip=document.getElementById("tip");
        const palette=["#2563eb","#16a34a","#d97706","#9333ea","#0891b2","#db2777","#65a30d","#dc2626"],roles={};
        let nodes=[],wires=[],yaw=0.6,pitch=0.35,drag=null,auto=true,hover=null;
        const color=r=>roles[r]??(roles[r]=palette[Object.keys(roles).length%palette.length]);
        function place(list){const n=list.length;list.forEach((v,i)=>{const y=1-2*(i+.5)/n,r=Math.sqrt(1-y*y),t=i*2.399963;v.p=[Math.cos(t)*r,y,Math.sin(t)*r]})}
        async function load(){try{const s=await (await fetch("/brain.json")).json();const old=Object.fromEntries(nodes.map(v=>[v.id,v]));
        nodes=s.nodes.map(v=>Object.assign(old[v.id]??{},v));place(nodes);const at=Object.fromEntries(nodes.map(v=>[v.id,v]));
        wires=s.wires.map(w=>({a:at[w.from],b:at[w.to],s:w.signal})).filter(w=>w.a&&w.b);
        const live=nodes.filter(v=>v.run).length;document.getElementById("count").textContent=`${nodes.length} neurons · ${wires.length} wires · ${live} live`;
        document.getElementById("legend").innerHTML=[...new Set(nodes.map(v=>v.role))].map(r=>`<span><span style="color:${color(r)}">●</span> ${r}</span>`).join("")}catch(e){}}
        function project(p,w,h){const cy=Math.cos(yaw),sy=Math.sin(yaw),cp=Math.cos(pitch),sp=Math.sin(pitch);
        let X=p[0]*cy-p[2]*sy,Z=p[0]*sy+p[2]*cy,Y=p[1]*cp-Z*sp;Z=p[1]*sp+Z*cp;const f=Math.min(w,h)*.38,d=2.6/(2.6+Z);return[w/2+X*f*d,h/2+Y*f*d,d,Z]}
        function frame(t){const w=c.width=innerWidth*devicePixelRatio,h=c.height=innerHeight*devicePixelRatio;x.scale(1,1);
        if(auto)yaw+=0.0015;x.clearRect(0,0,w,h);const pr=new Map(nodes.map(v=>[v,project(v.p,w,h)]));
        x.lineWidth=devicePixelRatio;x.strokeStyle=getComputedStyle(document.body).getPropertyValue("--wire");
        for(const e of wires){const a=pr.get(e.a),b=pr.get(e.b);x.beginPath();x.moveTo(a[0],a[1]);x.lineTo(b[0],b[1]);x.stroke()}
        for(const v of [...nodes].sort((p,q)=>pr.get(q)[3]-pr.get(p)[3])){const[px,py,d]=pr.get(v);let r=(3+3*d)*devicePixelRatio;
        if(v.run){const k=(Math.sin(t/240)+1)/2;x.beginPath();x.arc(px,py,r*(1.8+k),0,7);x.fillStyle=v.waiting?"rgba(217,119,6,.25)":"rgba(37,99,235,.25)";x.fill()}
        x.beginPath();x.arc(px,py,r,0,7);x.fillStyle=color(v.role);x.globalAlpha=.35+.65*Math.min(1,d);x.fill();x.globalAlpha=1;v.s=[px/devicePixelRatio,py/devicePixelRatio,r/devicePixelRatio]}
        requestAnimationFrame(frame)}
        c.onpointerdown=e=>{drag=[e.clientX,e.clientY];auto=false};onpointerup=()=>drag=null;
        onpointermove=e=>{if(drag){yaw+=(e.clientX-drag[0])/200;pitch=Math.max(-1.4,Math.min(1.4,pitch+(e.clientY-drag[1])/200));drag=[e.clientX,e.clientY];return}
        hover=nodes.find(v=>v.s&&Math.hypot(v.s[0]-e.clientX,v.s[1]-e.clientY)<v.s[2]+4);
        if(hover){tip.style.display="block";tip.style.left=(e.clientX+12)+"px";tip.style.top=(e.clientY+12)+"px";
        tip.textContent=`${hover.label} (${hover.role})`+(hover.run?` — ${hover.waiting?"waiting: "+hover.waiting:"running: "+hover.run}`:"")}else tip.style.display="none"};
        load();setInterval(load,2000);requestAnimationFrame(frame);</script></body></html>
        """;
}
