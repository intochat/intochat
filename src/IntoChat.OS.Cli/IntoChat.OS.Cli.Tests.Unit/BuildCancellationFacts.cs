namespace IntoChat.OS.Cli.Tests.Unit;

public sealed class BuildCancellationFacts
{
    [Fact]
    public async Task CancellationCleansOnlyItsCandidateAndPreservesOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "intochat-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "App.csproj");
            await File.WriteAllTextAsync(project, "<Project/>", TestContext.Current.CancellationToken);
            var output = Path.Combine(root, "app.intochat-app");
            await File.WriteAllTextAsync(output, "active", TestContext.Current.CancellationToken);
            var process = new BlockingProcess();
            using var cancellation = new CancellationTokenSource();
            var build = new AppBuild(Path.Combine(root, "cache"), process).Pack(project, output, cancellation.Token);
            await process.Started.Task;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            Assert.Equal("active", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "cache")));
        }
        finally { await AppBuild.RemoveCandidate(root); }
    }

    [Fact]
    public async Task OverlappingBuildsKeepIndependentCandidates()
    {
        var root = Path.Combine(Path.GetTempPath(), "intochat-overlap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "App.csproj");
            await File.WriteAllTextAsync(project, "<Project/>", TestContext.Current.CancellationToken);
            var process = new OverlappingProcess();
            var builder = new AppBuild(Path.Combine(root, "cache"), process);
            using var firstCancellation = new CancellationTokenSource();
            using var secondCancellation = new CancellationTokenSource();
            var first = builder.Build(project, "Debug", firstCancellation.Token);
            var second = builder.Build(project, "Debug", secondCancellation.Token);
            await process.BothStarted.Task;
            Assert.Equal(2, process.Outputs.Distinct(StringComparer.Ordinal).Count());
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "cache")));
            Assert.False(second.IsCompleted);
            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "cache")));
        }
        finally { await AppBuild.RemoveCandidate(root); }
    }

    [Fact]
    public async Task ProcessCancellationTerminatesTheBuildAndItsChild()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = Path.Combine(Path.GetTempPath(), "intochat-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pid = Path.Combine(root, "child.pid");
            var script = Path.Combine(root, "child.ps1");
            await File.WriteAllTextAsync(script, "$PID | Set-Content -LiteralPath '" + pid.Replace("'", "''", StringComparison.Ordinal) + "'; Start-Sleep -Seconds 120", TestContext.Current.CancellationToken);
            var project = Path.Combine(root, "sleep.proj");
            await File.WriteAllTextAsync(project, "<Project><Target Name=\"Wait\"><Exec Command=\"powershell -NoProfile -ExecutionPolicy Bypass -File &quot;" + script + "&quot;\" /></Target></Project>", TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var build = new BuildProcess().Run(root, ["msbuild", project, "-t:Wait", "-nologo"], cancellation.Token);
            while (!File.Exists(pid)) { if (build.IsCompleted) { await build; } await Task.Delay(50, cancellation.Token); }
            var childId = int.Parse((await File.ReadAllTextAsync(pid, TestContext.Current.CancellationToken)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            try { using var child = System.Diagnostics.Process.GetProcessById(childId); using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await child.WaitForExitAsync(exit.Token); Assert.True(child.HasExited); }
            catch (ArgumentException) { }
        }
        finally { await AppBuild.RemoveCandidate(root); }
    }

    private sealed class OverlappingProcess : IBuildProcess
    {
        internal List<string> Outputs { get; } = [];
        internal TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Outputs.Add(arguments.Single(argument => argument.StartsWith("-p:OutDir=", StringComparison.Ordinal)));
            if (Outputs.Count == 2) { BothStarted.SetResult(); }
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }
    }

    private sealed class BlockingProcess : IBuildProcess
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }
    }
}
