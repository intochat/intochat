using DigitalBrain.Platform.Workshop;

namespace DigitalBrain.Platform.Tests.Unit;

// The Workshop never writes outside its configured root, and never touches version control or
// configuration that carries secrets.
public sealed class WorkshopSandboxFacts
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "digitalbrain-workshop");

    [Fact]
    public void APathInsideTheRootResolves()
    {
        Assert.True(WorkshopSandbox.TryResolve(Root, "src/Module/File.cs", out var full));
        Assert.Equal(Path.Combine(Root, "src", "Module", "File.cs"), full);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData(".git/config")]
    [InlineData("src/appsettings.json")]
    [InlineData("secrets/vault")]
    public void APathOutsideOrSensitiveIsRefused(string path)
        => Assert.False(WorkshopSandbox.TryResolve(Root, path, out _));

    [Fact]
    public void AnEmptyRootIsRefused()
        => Assert.False(WorkshopSandbox.TryResolve("", "src/File.cs", out _));
}
