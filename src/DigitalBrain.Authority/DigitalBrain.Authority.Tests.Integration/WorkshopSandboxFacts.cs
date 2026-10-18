using DigitalBrain.Authority.Workshop;

namespace DigitalBrain.Authority.Tests.Integration;

// The Workshop never writes outside its configured root, and never touches version control or
// configuration that carries secrets.
public sealed class WorkshopSandboxFacts
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "digitalbrain-workshop");

    [Fact]
    public void APathInsideTheRootResolves()
    {
        Assert.True(WorkshopSandbox.TryResolve(Root, "code/Module/File.cs", out var full));
        Assert.Equal(Path.Combine(Root, "code", "Module", "File.cs"), full);
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("code/../../outside.cs")]
    [InlineData(".git/config")]
    [InlineData("code/appsettings.json")]
    [InlineData("secrets/vault")]
    public void APathOutsideOrSensitiveIsRefused(string path)
        => Assert.False(WorkshopSandbox.TryResolve(Root, path, out _));

    [Fact]
    public void AnEmptyRootIsRefused()
        => Assert.False(WorkshopSandbox.TryResolve("", "code/File.cs", out _));
}
