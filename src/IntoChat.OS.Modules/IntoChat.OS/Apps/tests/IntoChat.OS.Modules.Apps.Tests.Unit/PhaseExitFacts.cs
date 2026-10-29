namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// Phase 08 exit: the 0.7.0 tree has no analyzer-suppression ratchets. Deferred apps stay folder scripts;
// they are not exempted by a marker file.
public sealed class PhaseExitFacts
{
    [Fact]
    public void FeaturesAndSrcHaveNoSuppressMarkers()
    {
        var root = Repository();
        var found = new List<string>();
        foreach (var start in new[] { "features", "src" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, start), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name is ".dbsuppress" or ".baseline-red") { found.Add(file); }
            }
        }
        Assert.Empty(found);
    }

    [Fact]
    public void DeferredAppsRemainFolderScripts()
    {
        var root = Path.Combine(Repository(), "features", "os");
        foreach (var name in new[] { "Ino", "Memory", "Circle", "CircleRoom", "Friends", "Engineer", "SelfImprove", "Router", "Usage", "DataVisualizer", "BrainView", "Calculator", "FilmCraft", "PhotoCraft" })
        {
            Assert.True(File.Exists(Path.Combine(root, name, "app.cs")), name);
        }
        foreach (var compiled in new[] { "Workspace", "Desktop", "Settings", "Apps" })
        {
            Assert.False(Directory.Exists(Path.Combine(root, compiled)), compiled);
        }
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
