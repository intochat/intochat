using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class DevReloadFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dev-apps-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ABrokenEditKeepsThePreviousLocalBundle()
    {
        var catalog = new FeatureCatalog(directory: null, new Gate(files => files.Values.All(value => !value.Contains("nope", StringComparison.Ordinal))), new FeatureDirectory(_root, writable: true));
        await catalog.AcceptLocal(Bundle("1.0.0", "static class Note { }\n"));
        Assert.Equal("1.0.0", (await catalog.Find(FeatureCatalog.DevOwner, "notes"))!.Version);
        Assert.Null(await catalog.Find("os", "notes"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.AcceptLocal(Bundle("1.0.1", "static class Note { public const string Bad = \"nope\"; }\n")));
        Assert.Equal("1.0.0", (await catalog.Find(FeatureCatalog.DevOwner, "notes"))!.Version);
    }

    [Fact]
    public void TopologyOtherThanLocalHidesTheRoute()
    {
        Assert.True(DevAppAccess.IsLocal(new ConfigurationBuilder().Build()));
        Assert.True(DevAppAccess.IsLocal(new ConfigurationBuilder().AddInMemoryCollection([new("IntoChatOS:Topology", "local")]).Build()));
        Assert.False(DevAppAccess.IsLocal(new ConfigurationBuilder().AddInMemoryCollection([new("IntoChatOS:Topology", "cloud")]).Build()));
    }

    private static AppBundle Bundle(string version, string source)
        => FeatureFolder.ToBundle("notes", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AppFiles.DeclarationPath] = Declarations.Of("notes", version, "Notes", "Notes."),
            ["note.cs"] = source,
        });

    private sealed class Gate(Func<IReadOnlyDictionary<string, string>, bool> success) : IScriptSandbox
    {
        public bool CanRun => true;
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken) => Task.FromResult<Verdict[]>([]);
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(success(files)
                ? new ScriptCompilationCheck(true, [])
                : new ScriptCompilationCheck(false, [new ScriptCompilationDiagnostic("note.cs", "CS1002", 1, "; expected")]));
    }
}
