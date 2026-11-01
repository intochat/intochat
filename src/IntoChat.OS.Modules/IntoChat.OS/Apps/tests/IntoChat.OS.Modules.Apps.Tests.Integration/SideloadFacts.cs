using DigitalBrain.UI;
using IntoChat.OS.Runtime.Storage.Confinement;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class SideloadFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sideload-").FullName;
    private readonly List<GitCloneRequest> _git = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AFolderIsSideloadEvenWhenItClaimsOs()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "os"));
        File.WriteAllText(Path.Combine(folder.FullName, "app.cs"), Declaration("notes", "# Notes\nRole: Shell\nShipped: true\n"));
        File.WriteAllText(Path.Combine(folder.FullName, "note.cs"), "static class Note { }\n");
        var catalog = Catalog();

        var bundle = await catalog.Find(FeatureCatalog.SideloadOwner, "notes");
        Assert.NotNull(bundle);
        Assert.Contains("Role: Shell", bundle.Spec, StringComparison.Ordinal);
        Assert.Contains("Shipped: true", bundle.Spec, StringComparison.Ordinal);
        Assert.Null(await catalog.Find("os", "notes"));
        var listing = Assert.Single(await catalog.Listings(), item => item.Name == "notes");
        Assert.Equal(FeatureCatalog.SideloadOwner, listing.Publisher);
        Assert.IsType<AppSource.Shipped>(listing.Source);
        var snapshot = new AppSnapshot("notes", new AppManifest("Notes", "Notes.", [], [], []), "", new Dictionary<string, string>(), new Dictionary<string, string>(), new Dictionary<string, string>(), "1.0.0", AppStatus.Live, [], [],
            Origin: new AppOrigin(FeatureCatalog.SideloadOwner, new AppSource.Shipped(), "notes", "1.0.0"));
        Assert.False(ShippedApps.Is(snapshot, "notes"));
        Assert.False(ShippedApps.Roots.Contains("notes"));
    }

    [Fact]
    public void AMissingAppsDirectoryIsEmpty()
    {
        var scan = new SideloadDirectory(Path.Combine(_root, "missing")).Scan();
        Assert.Empty(scan.Features);
        Assert.Empty(scan.Failures);
    }

    [Fact]
    public void APathOutsideTheFolderIsRejected()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "notes"));
        var error = Assert.Throws<ConfinementDenied>(() => SideloadDirectory.RequireInside(folder.FullName, Path.Combine(folder.FullName, "..", "secret.txt")));
        Assert.Equal(ConfinementReason.Outside, error.Reason);
    }

    [Fact]
    public async Task ALinkInsideTheFolderIsRejected()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside"));
        File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), "nope");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "notes"));
        File.WriteAllText(Path.Combine(folder.FullName, "app.cs"), Declaration("notes"));
        var link = Path.Combine(folder.FullName, "link");
        try { Directory.CreateSymbolicLink(link, outside.FullName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.FullName}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            made?.WaitForExit();
            if (!Directory.Exists(link)) { throw new InvalidOperationException("This environment cannot create a directory link.", error); }
        }

        var scan = new SideloadDirectory(_root).Scan();
        Assert.Empty(scan.Features);
        Assert.Contains(scan.Failures, failure => failure.Reason == nameof(ConfinementReason.LinkEscape));
    }

    [Fact]
    public async Task GitArgumentsPlaceTheSeparatorBeforeTheUrlAndRejectUnsafeUrls()
    {
        var git = new GitSideload(Record, GitSideload.MaxBytes);
        var name = await git.CloneInto(_root, "https://example.com/notes.git", CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(name, "app.cs")) || File.Exists(Path.Combine(name, "app.cs")));
        var request = Assert.Single(_git);
        var split = request.Arguments.ToList().IndexOf("--");
        Assert.True(split > 0);
        Assert.Equal("https://example.com/notes.git", request.Arguments[split + 1]);
        Assert.Equal(name, request.Arguments[split + 2]);
        Assert.Contains("core.hooksPath=/dev/null", request.Arguments);
        Assert.Equal(GitSideload.AllowProtocol, request.AllowProtocol);
        Assert.Equal(GitSideload.Timeout, request.Timeout);
        Assert.Equal(50L * 1024 * 1024, GitSideload.MaxBytes);
        GitSideload.RequireUrl("git@github.com:acme/notes.git");
        GitSideload.RequireUrl("ssh://git@github.com/acme/notes.git");

        Assert.Empty(_git.Skip(1));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "--upload-pack=touch /tmp/x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "-u payload", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "ext::helper", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "fd::0", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "file:///tmp/repo", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => git.CloneInto(_root, "/tmp/repo", CancellationToken.None));
        Assert.Single(_git);
    }

    [Fact]
    public async Task ACloneLargerThanTheCapIsDeleted()
    {
        var git = new GitSideload(WriteBytes, maxBytes: 4);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => git.CloneInto(_root, "https://example.com/notes.git", CancellationToken.None));
        Assert.Contains("larger than 4", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateDirectories(_root), folder => Path.GetFileName(folder).StartsWith("_clone-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallCallsTheStorePathAndAnAnalyzerErrorBlocksIt()
    {
        WriteApp("notes", "static class Note { }\n");
        WriteApp("bad", "static class Note { void Swallow() { try { } catch { } } }\n");
        var calls = new List<string>();
        var installer = Installer(calls);

        var installed = await installer.Install("notes", CancellationToken.None);
        Assert.True(installed.Ok);
        Assert.Equal("Installed notes.", installed.Message);
        Assert.Equal(["sideload/notes"], calls);

        var blocked = await installer.Install("bad", CancellationToken.None);
        Assert.False(blocked.Ok);
        Assert.Contains("DB0022", blocked.Message, StringComparison.Ordinal);
        Assert.Single(calls);
    }

    [Fact]
    public async Task SideloadAppRequiringCapabilitiesFailsWithoutConsentAndSucceedsWithConsent()
    {
        var requires = new AppRequirements(SemanticTypes: ["person.profile"]);
        WriteAppWithRequirements("secure-notes", "static class Note { }\n", requires);
        var calls = new List<string>();
        var installer = Installer(calls);

        var rejected = await installer.Install("secure-notes", CancellationToken.None);
        Assert.False(rejected.Ok);
        Assert.Contains("Consent required", rejected.Message, StringComparison.Ordinal);
        Assert.Empty(calls);

        var wrongConsent = await installer.Install("secure-notes", "wrong-consent-digest", CancellationToken.None);
        Assert.False(wrongConsent.Ok);
        Assert.Contains("Consent required", wrongConsent.Message, StringComparison.Ordinal);
        Assert.Empty(calls);

        var validConsent = AppConsent.Of(requires);
        var accepted = await installer.Install("secure-notes", validConsent, CancellationToken.None);
        Assert.True(accepted.Ok);
        Assert.Equal("Installed secure-notes.", accepted.Message);
        Assert.Equal(["sideload/secure-notes"], calls);
    }

    [Fact]
    public async Task SettingsPresentsTheListTheInstallAndTheClone()
    {
        WriteApp("notes", "static class Note { }\n");
        var calls = new List<string>();
        var board = new Board();
        var app = new SettingsApp(board, new SideloadDesk(Installer(calls)));

        await app.SideloadList(new SettingsSideloadList());
        var listed = Assert.IsType<Collection>(board.Main!.Root);
        var entry = Assert.Single(listed.Entries);
        Assert.Equal("notes", entry.Id);
        Assert.Equal("not installed", entry.Subtitle);

        await app.SideloadInstall(new SettingsSideloadInstall("notes"));
        Assert.Equal("Installed notes.", Assert.IsType<Text>(board.Main.Root).Value);
        Assert.Equal(["sideload/notes"], calls);

        var git = new GitSideload(Record, GitSideload.MaxBytes);
        var adding = new SettingsApp(board, new SideloadDesk(new SideloadInstaller(Catalog(git), git, (request, _) =>
        {
            calls.Add(request.Ref.Publisher + "/" + request.Ref.App);
            return Task.FromResult(InstallOutcome.Installed(request.Ref.App));
        })));
        await adding.SideloadAddGit(new SettingsSideloadAddGit("https://example.com/notes.git"));
        Assert.Equal("Cloned notes.", Assert.IsType<Text>(board.Main.Root).Value);
        Assert.NotNull(await Catalog(git).Find(FeatureCatalog.SideloadOwner, "notes"));
    }

    private SideloadInstaller Installer(List<string> calls)
    {
        var git = new GitSideload(Record, GitSideload.MaxBytes);
        return new SideloadInstaller(Catalog(git), git, (request, _) =>
        {
            calls.Add(request.Ref.Publisher + "/" + request.Ref.App);
            return Task.FromResult(InstallOutcome.Installed(request.Ref.App));
        });
    }

    private FeatureCatalog Catalog(GitSideload? git = null)
        => new(directory: null, new Gate(), dev: null, sideload: new SideloadDirectory(_root), git: git);

    private void WriteApp(string name, string source)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(folder.FullName, "app.cs"), Declaration(name));
        File.WriteAllText(Path.Combine(folder.FullName, "note.cs"), source);
    }

    private void WriteAppWithRequirements(string name, string source, AppRequirements requires)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(folder.FullName, "app.cs"), AppDeclaration.Write(new(name, "1.0.0", new AppManifest("SecureNotes", "Notes.", [], [], [], Requires: requires), "")));
        File.WriteAllText(Path.Combine(folder.FullName, "note.cs"), source);
    }

    private static string Declaration(string name, string spec = "")
        => AppDeclaration.Write(new(name, "1.0.0", new AppManifest("Notes", "Notes.", [], [], []), spec));

    private Task<int> Record(GitCloneRequest request, CancellationToken cancellationToken)
    {
        _git.Add(request);
        var target = request.Arguments[^1];
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "app.cs"), Declaration("notes"));
        File.WriteAllText(Path.Combine(target, "note.cs"), "static class Note { }\n");
        return Task.FromResult(0);
    }

    private static Task<int> WriteBytes(GitCloneRequest request, CancellationToken cancellationToken)
    {
        var target = request.Arguments[^1];
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "blob.bin"), [1, 2, 3, 4, 5]);
        return Task.FromResult(0);
    }

    private sealed class Board : DigitalBrain.UI.IPresenter
    {
        public DigitalBrain.UI.View? Main { get; private set; }
        public void Present(DigitalBrain.UI.View? side, DigitalBrain.UI.View? main) => Main = main;
        public void Toast(Toast toast) => _ = toast;
    }

    private sealed class Gate : IScriptSandbox
    {
        public bool CanRun => true;
        public string AuthoringDescription => "";
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, object signal, CancellationToken cancellationToken) => Task.FromResult<Verdict[]>([]);
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptContractCatalog> ReadContractDetail(string moduleId, string contractName, string? methodName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
            => Task.FromResult(files.Values.All(value => !value.Contains("nope", StringComparison.Ordinal))
                ? new ScriptCompilationCheck(true, [])
                : new ScriptCompilationCheck(false, [new ScriptCompilationDiagnostic("note.cs", "CS1002", 1, "; expected")]));
    }
}
