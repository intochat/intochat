using System.IO.Compression;
using System.Text;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Packages;

public sealed class AppPackageArchiveFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The verifier of a host with no compiler of its own: the default reference set.
    private static readonly IUnitVerifier Verifier = new UnitVerifier();

    private static string LocateHelloWorldFolder()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "helloworld");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "HelloWorld.cs")))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate apps/helloworld folder.");
    }

    [Fact]
    public async Task ExportAndImportZipAndDirectoryRoundTripPreservesCodeScenariosSkillAndSpec()
    {
        var helloDir = LocateHelloWorldFolder();
        var studio = new RecordingSynapseStudio();

        // 1. Import from apps/helloworld folder (verifies Roslyn + PE boundary + scenarios before deploy)
        var folderImport = await AppPackageArchive.ImportFromDirectoryAsync(helloDir, Verifier, studio, ct: Ct);
        Assert.True(folderImport.Succeeded, string.Join("\n", folderImport.Errors));
        Assert.Equal("helloworld", folderImport.Alias);
        Assert.NotNull(folderImport.Skill);
        Assert.Equal("Hello World", folderImport.Skill!.Title);
        Assert.Equal(1, studio.DeployCount);
        Assert.Equal(
            ["HelloWorld.cs", "SKILL.md", "scenarios.json", "spec.md"],
            folderImport.Files.Keys.Order(StringComparer.Ordinal).ToArray());

        // 2. Export deployed unit to ZIP archive via ISynapseStudio
        var zipBytes = await AppPackageArchive.ExportZipAsync("helloworld", studio, Ct);
        Assert.NotEmpty(zipBytes);

        // 3. Re-import ZIP into a fresh studio and verify identical files + deployment
        var freshStudio = new RecordingSynapseStudio();
        var zipImport = await AppPackageArchive.ImportZipAsync(zipBytes, Verifier, freshStudio, ct: Ct);
        Assert.True(zipImport.Succeeded, string.Join("\n", zipImport.Errors));
        Assert.Equal("helloworld", zipImport.Alias);
        Assert.Equal(1, freshStudio.DeployCount);
        foreach (var (name, expectedContent) in folderImport.Files)
        {
            Assert.Equal(expectedContent, zipImport.Files[name]);
        }

        // 4. Export to a temporary directory and re-import
        var tempDir = Path.Combine(Path.GetTempPath(), "intochat-pkg-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await AppPackageArchive.ExportToDirectoryAsync("helloworld", tempDir, freshStudio, Ct);
            Assert.True(File.Exists(Path.Combine(tempDir, "HelloWorld.cs")));
            Assert.True(File.Exists(Path.Combine(tempDir, "scenarios.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(tempDir, "spec.md")));

            var reimported = await AppPackageArchive.ImportFromDirectoryAsync(tempDir, Verifier, ct: Ct);
            Assert.True(reimported.Succeeded, string.Join("\n", reimported.Errors));
            Assert.Equal("helloworld", reimported.Alias);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportRejectsZipSlipDisallowedFilesCompileErrorsAndFailingScenariosBeforeDeploy()
    {
        var studio = new RecordingSynapseStudio();

        // 1. ZipSlip path traversal entry rejected
        var zipSlipBytes = BuildRawZip(("../evil.cs", "// traversal"));
        var zipSlipResult = await AppPackageArchive.ImportZipAsync(zipSlipBytes, Verifier, studio, ct: Ct);
        Assert.False(zipSlipResult.Succeeded);
        Assert.Equal(0, studio.DeployCount);

        // 2. Disallowed executable file rejected
        var exeZipBytes = BuildRawZip(("payload.exe", "MZ"), ("Unit.cs", "[DigitalBrain.State(\"bad\")] public record S;"));
        var exeResult = await AppPackageArchive.ImportZipAsync(exeZipBytes, Verifier, studio, ct: Ct);
        Assert.False(exeResult.Succeeded);
        Assert.Equal(0, studio.DeployCount);

        // 3. Malformed C# rejected by Roslyn before deploy
        var badCsZip = AppPackageArchive.ExportZip("broken", new Dictionary<string, string>
        {
            ["Broken.cs"] = "using DigitalBrain; [State(\"broken\")] public sealed record BrokenState( { syntax error",
        });
        var badCsResult = await AppPackageArchive.ImportZipAsync(badCsZip, Verifier, studio, ct: Ct);
        Assert.False(badCsResult.Succeeded);
        Assert.NotEmpty(badCsResult.Errors);
        Assert.Equal(0, studio.DeployCount);

        // 4. Failing scenario rejected by BuilderTools.Check before deploy
        var helloDir = LocateHelloWorldFolder();
        var goodCode = await File.ReadAllTextAsync(Path.Combine(helloDir, "HelloWorld.cs"), Ct);
        var redScenarioZip = AppPackageArchive.ExportZip("helloworld", new Dictionary<string, string>
        {
            ["HelloWorld.cs"] = goodCode,
            ["scenarios.json"] = """
                [
                  {
                    "name": "wrong-expectation",
                    "given": {},
                    "when": [],
                    "then": ["state helloworld Clicks = 999"]
                  }
                ]
                """,
        });
        var redResult = await AppPackageArchive.ImportZipAsync(redScenarioZip, Verifier, studio, ct: Ct);
        Assert.False(redResult.Succeeded);
        Assert.Contains(redResult.Errors, e => e.Contains("wrong-expectation", StringComparison.Ordinal));
        Assert.Equal(0, studio.DeployCount);
    }

    private static byte[] BuildRawZip(params (string EntryName, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return ms.ToArray();
    }

    private sealed class RecordingSynapseStudio : ISynapseStudio
    {
        private readonly Dictionary<string, Dictionary<string, string>> _sources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _sidecars = new(StringComparer.Ordinal);

        public int DeployCount { get; private set; }

        public Task<InoToolResult> Check(string source) => Task.FromResult(new InoToolResult("ok"));

        public Task<InoToolResult> Deploy(string alias, string source)
            => Task.FromResult(new InoToolResult("deployed"));

        public Task<UnitDeployResult> DeployFiles(string alias, IReadOnlyDictionary<string, string> files, IReadOnlyDictionary<string, string> bindings)
        {
            DeployCount++;
            var code = UnitFiles.Code(files);
            _sources[alias] = new Dictionary<string, string>(code, StringComparer.Ordinal);
            _sidecars[alias] = files.Where(kv => !code.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            return Task.FromResult(new UnitDeployResult(
                true,
                $"{alias} deployed",
                DeployStatus.Deployed,
                "hash-1",
                new Dictionary<string, DigitalBrain.Units.InstallOutcome> { [alias] = new(DigitalBrain.Units.InstallStatus.Unchanged, "", null) }));
        }

        public Task<InoToolResult> Retire(string alias) => Task.FromResult(new InoToolResult("retired"));

        public Task<DeployedSource[]> List() => Task.FromResult(
            _sources.Keys.Select(a => new DeployedSource(a, "hash-1", [a], DeployStatus.Deployed, null, DateTimeOffset.UtcNow, null)).ToArray());

        public Task<Dictionary<string, string>> Sidecars(string alias)
            => Task.FromResult(_sidecars.TryGetValue(alias, out var s) ? new Dictionary<string, string>(s) : []);

        public Task<Dictionary<string, string>> SourceFiles(string alias)
            => Task.FromResult(_sources.TryGetValue(alias, out var c) ? new Dictionary<string, string>(c) : []);

        public Task<InoToolResult> Failures(string alias) => Task.FromResult(new InoToolResult("none"));
    }
}
