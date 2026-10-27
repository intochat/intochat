using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Flutter;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsFilesEntryFacts
{
    [Fact]
    public void TheShippedManifestDeclaresStorageSelection()
    {
        var bundle = SolutionProjects.FeaturesOf("os").Single(app => app.Name == "files");
        Assert.Contains(bundle.Manifest.Operations, operation => operation.Name == "storage");
        Assert.Contains("FilesEntry.cs", bundle.Files.Keys);
    }

    [Fact]
    public void TheShippedPresentationNamesTheFullDesktopView()
    {
        var presentation = FilesEntry.Presentation("owner/apps/files");
        Assert.Equal(new(UIVocabulary.ViewType, "owner/apps/files/side"), presentation.Side);
        Assert.Equal(new(UIVocabulary.ViewType, "owner/apps/files/main"), presentation.Main);
        var full = Assert.Single(presentation.Views ?? [], view => view.Id == "full");
        Assert.Equal(new(UIVocabulary.ViewType, "owner/apps/files/full"), full.Root);
    }

    [Fact]
    public async Task StorageSelectionForwardsTheIndexAndRequestIdentity()
    {
        var workspace = new Workspace();
        var request = new AppRequested("storage", "{\"Input\":{\"Index\":2}}", Guid.NewGuid());
        Assert.Null(await FilesEntry.Forward(workspace, request, TestContext.Current.CancellationToken));
        Assert.Equal(("storage", 2, 17L, request.RequestId), Assert.Single(workspace.Calls));
        Assert.Equal(1, workspace.RevisionReads);
    }

    [Theory]
    [InlineData("mkdir")]
    [InlineData("write")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("paste")]
    public async Task WritesNeverReplaceAnAbsentOrExplicitExpectedRevision(string operation)
    {
        var workspace = new Workspace();
        Assert.NotNull(await FilesEntry.Forward(workspace, new(operation, "{}", Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Calls);
        Assert.Equal(0, workspace.RevisionReads);
        var request = new AppRequested(operation, "{\"ExpectedRevision\":6}", Guid.NewGuid());
        Assert.Null(await FilesEntry.Forward(workspace, request, TestContext.Current.CancellationToken));
        Assert.Equal((operation, null, 6L, request.RequestId), Assert.Single(workspace.Calls));
        Assert.Equal(0, workspace.RevisionReads);
    }

    private sealed class Workspace : IFilesWorkspace
    {
        public List<(string Operation, int? Index, long Revision, Guid RequestId)> Calls { get; } = [];
        public int RevisionReads { get; private set; }
        public Task Open(CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> Revision() { RevisionReads++; return Task.FromResult(17L); }
        public Task Request(string operation, FilesWorkspaceInput input, long expectedRevision, Guid requestId, CancellationToken ct = default)
        { Calls.Add((operation, input.Index, expectedRevision, requestId)); return Task.CompletedTask; }
    }
}
