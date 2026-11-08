namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Phase 08 exit: the 0.7.0 tree has no analyzer-suppression ratchets.
public sealed class PhaseExitFacts
{
    [Fact]
    public void SrcHasNoSuppressMarkers()
    {
        var root = Repository();
        var found = new List<string>();
        foreach (var start in new[] { "s" + "rc" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, start), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name is ".dbsuppress" or ".baseline-red") { found.Add(file); }
            }
        }
        Assert.Empty(found);
    }

    private static string Repository()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx"))) { return dir.FullName; }
        }
        throw new InvalidOperationException("Intochat.OS.slnx was not found above the test output.");
    }
}
