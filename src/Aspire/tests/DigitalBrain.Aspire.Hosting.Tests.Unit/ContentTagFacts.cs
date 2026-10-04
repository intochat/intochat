namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ContentTagFacts : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("content-tag-").FullName;

    [Fact]
    public void TheSameTreeAlwaysCarriesTheSameTag()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "b.txt"), "beta");

        Assert.Equal(ContentTag.Of(_root), ContentTag.Of(_root));
        Assert.Equal(16, ContentTag.Of(_root).Length);
    }

    [Fact]
    public void AChangedByteOrARenamedFileChangesTheTag()
    {
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "alpha");
        var original = ContentTag.Of(_root);

        File.WriteAllText(file, "alphb");
        var edited = ContentTag.Of(_root);
        File.Move(file, Path.Combine(_root, "renamed.txt"));
        var renamed = ContentTag.Of(_root);

        Assert.NotEqual(original, edited);
        Assert.NotEqual(edited, renamed);
    }

    [Fact]
    public void BuildOutputDirectoriesDoNotAffectTheTag()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        var clean = ContentTag.Of(_root);

        foreach (var noise in new[] { "bin", "obj", "build", ".dart_tool" })
        {
            Directory.CreateDirectory(Path.Combine(_root, noise));
            File.WriteAllText(Path.Combine(_root, noise, "artifact"), Guid.NewGuid().ToString());
        }

        Assert.Equal(clean, ContentTag.Of(_root));
    }

    [Fact]
    public void ExtraInputsSaltTheTag()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");

        Assert.NotEqual(ContentTag.Of(_root), ContentTag.Of(_root, "3.47.2"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
