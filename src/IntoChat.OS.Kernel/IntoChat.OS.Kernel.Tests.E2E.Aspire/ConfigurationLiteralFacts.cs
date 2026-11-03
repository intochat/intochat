namespace IntoChat.OS.Kernel.Tests.E2E.Aspire;

public sealed class ConfigurationLiteralFacts
{
    [Fact]
    public void NoProductKeyIsSpelledInline()
    {
        var prefix = "IntoChatOS" + ":";
        var projectDir = Path.Combine(OsAppHostFixture.RepoRoot(), "s" + "rc", "IntoChat.OS.Kernel", "IntoChat.OS.Kernel.Tests.E2E.Aspire");
        var offenders = Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => File.ReadAllText(path).Contains("\"" + prefix, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(projectDir, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }
}
