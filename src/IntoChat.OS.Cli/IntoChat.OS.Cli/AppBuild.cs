using System.Text.Json;
using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Cli;

/// <summary>Builds trusted local projects into immutable validated candidates.</summary>
public sealed class AppBuild
{
    private readonly string cache;
    private readonly IBuildProcess process;

    public AppBuild(string? cacheDirectory = null) : this(cacheDirectory, new BuildProcess()) { }
    internal AppBuild(string? cacheDirectory, IBuildProcess process)
    {
        cache = Path.GetFullPath(cacheDirectory ?? Path.Combine(Path.GetTempPath(), "intochat-app-builds"));
        this.process = process;
    }

    public async Task<ValidatedAppPackage> Build(string project, string configuration, CancellationToken ct)
    {
        project = Path.GetFullPath(project);
        if (!File.Exists(project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Specify an existing .csproj file.", nameof(project)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ct.ThrowIfCancellationRequested();
        var candidate = Path.Combine(cache, ".build-" + Guid.NewGuid().ToString("N"));
        var delivery = Path.Combine(candidate, "delivery");
        var compiled = Path.Combine(candidate, "compiled");
        Directory.CreateDirectory(delivery);
        try
        {
            string[] properties = ["-p:Configuration=" + configuration, "-p:OutDir=" + compiled + Path.DirectorySeparatorChar,
                "-p:ArtifactsPath=" + Path.Combine(candidate, "artifacts"), "-p:UseArtifactsOutput=true", "-p:CodeGraphRefresh=false", "-p:DebugType=None", "-p:DebugSymbols=false", "-p:UseSharedCompilation=false"];
            var directory = Path.GetDirectoryName(project)!;
            await process.Run(directory, ["publish", project, "--nologo", "--output", delivery, .. properties], ct).ConfigureAwait(false);
            var evaluated = await process.Run(directory, ["msbuild", project, "--nologo", "-getProperty:TargetPath,TargetFramework", .. properties], ct).ConfigureAwait(false);
            using var json = JsonDocument.Parse(evaluated);
            var values = json.RootElement.GetProperty("Properties");
            var target = Path.GetFullPath(values.GetProperty("TargetPath").GetString()!);
            if (values.GetProperty("TargetFramework").GetString() != "net11.0")
            { throw new InvalidOperationException("Compiled apps must target net11.0."); }
            if (!target.StartsWith(compiled + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(target))
            { throw new InvalidOperationException("MSBuild TargetPath escaped the isolated compiled directory or was not produced."); }
            var metadata = Path.Combine(candidate, "metadata.json");
            var cli = typeof(AppBuild).Assembly.Location;
            await process.Run(directory, ["exec", "--runtimeconfig", Path.ChangeExtension(cli, ".runtimeconfig.json"), cli, "__extract-app-metadata", Path.Combine(delivery, Path.GetFileName(target)), metadata], ct).ConfigureAwait(false);
            var descriptor = JsonSerializer.Deserialize<CompiledAppDescriptor>(await File.ReadAllTextAsync(metadata, ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException("Metadata extraction returned no descriptor.");
            await using var archive = new MemoryStream();
            await new AppPackageWriter().Write(descriptor, delivery, archive, ct).ConfigureAwait(false);
            archive.Position = 0;
            return await new AppPackageReader(sharedAssemblies: descriptor.SharedAssemblies).Read(archive, Path.Combine(cache, "validated"), ct).ConfigureAwait(false);
        }
        finally { await RemoveCandidate(candidate).ConfigureAwait(false); }
    }

    internal static async Task RemoveCandidate(string candidate)
    {
        // Windows may release a terminated descendant's working directory just after its output pipes close.
        for (var attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(candidate)) { Directory.Delete(candidate, true); } return; }
            catch (IOException) when (attempt < 40) { await Task.Delay(50, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    public async Task<ValidatedAppPackage> Pack(string project, string output, CancellationToken ct)
    {
        var package = await Build(project, "Release", ct).ConfigureAwait(false);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var candidate = output + ".candidate-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await new AppPackageWriter().Write(package.Descriptor, package.DirectoryPath, stream, ct).ConfigureAwait(false); }
            ct.ThrowIfCancellationRequested();
            File.Move(candidate, output, true);
            return package;
        }
        finally { File.Delete(candidate); }
    }
}
