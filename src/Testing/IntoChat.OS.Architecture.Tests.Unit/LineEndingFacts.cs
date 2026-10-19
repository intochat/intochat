namespace IntoChat.OS.Architecture.Tests.Unit;

// The Linux build engine parses Dockerfiles and runs the scripts copied into images; a CRLF checkout (this repository's
// default on Windows) breaks both with errors that name the wrong line. .gitattributes pins them to LF.
public sealed class LineEndingFacts
{
    private static readonly string[] SkippedDirectories = ["artifacts", "obj", "bin", "node_modules", ".dart_tool", "build", ".git"];

    [Fact]
    public void GitAttributesPinsDockerfilesToLf()
    {
        var attributes = File.ReadAllLines(Path.Combine(Repository.Root, ".gitattributes")).Select(line => line.Trim()).ToArray();
        Assert.Contains("Dockerfile* text eol=lf", attributes);
        Assert.Contains("*.Dockerfile text eol=lf", attributes);
    }

    [Fact]
    public void EveryDockerfileAndEveryShebangScriptBesideOneIsLf()
    {
        var dockerfiles = Directory.EnumerateFiles(Repository.Root, "*Dockerfile*", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(path))
            .ToArray();
        Assert.NotEmpty(dockerfiles);
        var contexts = dockerfiles.Select(path => Path.GetDirectoryName(path)!).Distinct(StringComparer.OrdinalIgnoreCase);
        var scripts = contexts.SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Where(path => !IsSkipped(path) && StartsWithShebang(path));
        var crlf = dockerfiles.Concat(scripts).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => File.ReadAllBytes(path).Contains((byte)'\r'))
            .Select(Repository.Relative).Order(StringComparer.Ordinal).ToArray();
        Assert.True(crlf.Length == 0, "These files must use LF line endings:\n" + string.Join('\n', crlf));
    }

    private static bool IsSkipped(string path)
        => Path.GetRelativePath(Repository.Root, path).Split(Path.DirectorySeparatorChar).Any(part => SkippedDirectories.Contains(part, StringComparer.Ordinal));

    private static bool StartsWithShebang(string path)
    {
        Span<byte> head = stackalloc byte[2];
        using var stream = File.OpenRead(path);
        return stream.Read(head) == 2 && head[0] == '#' && head[1] == '!';
    }
}
