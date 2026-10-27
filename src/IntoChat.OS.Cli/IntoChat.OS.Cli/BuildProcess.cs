using System.Diagnostics;

namespace IntoChat.OS.Cli;

internal interface IBuildProcess
{
    Task<string> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct);
}

internal sealed class BuildProcess : IBuildProcess
{
    public async Task<string> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "DOTNET_HOST_PATH", "TESTINGPLATFORM_DOTNETTEST_EXECUTIONID" })
        { start.Environment.Remove(name); }
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var child = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = child.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await child.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        if (child.ExitCode != 0) { throw new InvalidOperationException($"dotnet exited {child.ExitCode}:\n{output}"); }
        return output;
    }
}
