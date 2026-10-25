using System.Diagnostics;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Workshop;

[GrainType("platform.workshop")]
internal sealed class WorkshopNeuron(IOptions<WorkshopOptions> options) : Grain, IWorkshop
{
    public async Task<WorkshopResult> Write(string path, string content, CancellationToken cancellationToken = default)
    {
        if (!WorkshopSandbox.TryResolve(options.Value.Root, path, out var full))
        { return new WorkshopResult(false, $"Path '{path}' is outside the workshop root."); }
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
        await File.WriteAllTextAsync(full, content, cancellationToken);
        return new WorkshopResult(true, full);
    }

    public async Task<WorkshopResult> Build(string project, CancellationToken cancellationToken = default)
    {
        if (!WorkshopSandbox.TryResolve(options.Value.Root, project, out var full))
        { return new WorkshopResult(false, $"Path '{project}' is outside the workshop root."); }
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("build");
        info.ArgumentList.Add(full);
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("q");
        info.ArgumentList.Add("--nologo");
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start dotnet.");
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            output += await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new WorkshopResult(process.ExitCode == 0, output);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new WorkshopResult(false, error.Message); }
    }
}
