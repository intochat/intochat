using System.ComponentModel;
using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using ModelContextProtocol.Server;

namespace DigitalBrain.Microsoft.CSharp;

[McpServerToolType]
public sealed class ScopedCSharpTools(IDigitalBrain brain, CSharpCatalogStore catalog, CSharpContractDiscovery? contracts, string scope, bool canRun)
{
    private const int LogTail = 150;

    public bool CanRun => canRun;

    [McpServerTool(Name = "csharp_contracts"), Description("Discover installed module IDs, neuron contracts with their signals, the #:project directive per module and an example single-file C# app. Pass modules=[] first, then select exact returned IDs (for example time and flutter).")]
    public Task<CSharpContractCatalogSnapshot> Contracts(string[] modules, CancellationToken ct) => RequireModule().Read(modules, ct);

    [McpServerTool(Name = "csharp_write"), Description("Save the full source of a single-file C# app and its readable name and purpose. The app connects with `await using var brain = await DigitalBrainClient.ConnectAsync(args);` and operates neurons through brain.Get<T>(id) and brain.On<TSignal>(neuron). Copy the #:project lines for the contracts you use from csharp_contracts. Saving does not restart a running app; use csharp_run start.")]
    public async Task<CSharpFileView> Write(string id, string source, string? name, string? purpose, CancellationToken ct)
    {
        // The catalog enforces name, purpose and per-workspace limits before any source is stored.
        var file = File(id);
        var description = await catalog.Describe(scope, id, name, purpose, ct);
        await file.Write(source, ct);
        return new(description, await file.Inspect(ct));
    }

    [McpServerTool(Name = "csharp_run"), Description("Act on a saved C# app: status reads its run state and recent output; start compiles and runs the current source in the C# sandbox (restarting it if running) and keeps it running: a crash is retried up to 5 times and a lost sandbox is restarted; stop stops it; logs reads recent console output; delete stops it and removes the file. Compile errors appear in logs with a non-zero exit code while the status is Restarting, then Exited. Never claim the app works until its logs show the expected output.")]
    public async Task<CSharpFileView> Run(string id, string action, CancellationToken ct) => (action ?? "").Trim().ToLowerInvariant() switch
    {
        "status" or "logs" => await Read(id, ct),
        "start" => await Start(id, ct),
        "stop" => await Stop(id, ct),
        "delete" => await Delete(id, ct),
        _ => throw new ArgumentException("Use action status, start, stop, logs or delete.", nameof(action)),
    };

    public async Task<IReadOnlyList<CSharpFileView>> List(CancellationToken ct)
    {
        var views = new List<CSharpFileView>();
        foreach (var description in await catalog.List(scope, ct))
        { views.Add(new(description, await File(description.Id).Inspect(ct))); }
        return views;
    }

    public async Task<CSharpFileView> Read(string id, CancellationToken ct)
    {
        var description = await catalog.Read(scope, id, ct);
        var file = File(id);
        return new(description, await file.Inspect(ct), await file.ReadLogs(LogTail, ct));
    }

    public async Task<CSharpFileView> Start(string id, CancellationToken ct)
    {
        if (!canRun) { throw new InvalidOperationException("This host has no C# sandbox, so it cannot run C# files."); }
        var description = await catalog.Read(scope, id, ct);
        var file = File(id);
        await file.Start(ct);
        return new(description, await file.Inspect(ct));
    }

    public async Task<CSharpFileView> Stop(string id, CancellationToken ct)
    {
        var description = await catalog.Read(scope, id, ct);
        var file = File(id);
        await file.Stop(ct);
        return new(description, await file.Inspect(ct));
    }

    public async Task<CSharpFileView> Delete(string id, CancellationToken ct)
    {
        var description = await catalog.Read(scope, id, ct);
        var file = File(id);
        await file.Delete(ct);
        await catalog.Remove(scope, id, ct);
        return new(description, await file.Inspect(ct));
    }

    private ICSharpFile File(string id)
    {
        RequireModule();
        return brain.Get<ICSharpFile>(CSharpFileKeys.For(scope, id));
    }

    private CSharpContractDiscovery RequireModule()
        => contracts ?? throw new InvalidOperationException("This host does not run C# files; compose CSharpModule.");
}
