using DigitalBrain.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace DigitalBrain.Modules.Microsoft.Roslyn.Tests.Unit;

public sealed class SolutionMapCompatibilityFacts
{
    // Produced with the exact 485f84c SolutionMap/ProjectNode/ProjectEdge sources before
    // migration, in an isolated project using Orleans 10.4.0 (no current contracts).
    [Theory]
    [InlineData("MAGAw/OeJ2NvZGluZy5zb2x1dGlvbi1tYXDoQBdsZWdhY3kuc2xueDEBILkt/nFTeXN0ZW0uQ29sbGVjdGlvbnMuR2VuZXJpYy5MaXN0YDFbW2NvZGluZy5wcm9qZWN0LW5vZGVdXQADIehADVNhbXBsZUEbU2FtcGxlLmNzcHJvakEJdGVzdAEN4OAxAXlULDJxU3lzdGVtLkNvbGxlY3Rpb25zLkdlbmVyaWMuTGlzdGAxW1tjb2RpbmcucHJvamVjdC1lZGdlXV0AAyHowA1BFURlcGVuZGVuY3ng4OA=")]
    [InlineData("MAGAw/OeJ2NvZGluZy5zb2x1dGlvbi1tYXDoQBdsZWdhY3kuc2xueDEB0CNBxitjb2RpbmcucHJvamVjdC1ub2RlW10AAyHoQA1TYW1wbGVBG1NhbXBsZS5jc3Byb2pBCXRlc3QBDeDgMQFm+XFtK2NvZGluZy5wcm9qZWN0LWVkZ2VbXQADIejADUEVRGVwZW5kZW5jeeDg4A==")]
    public void PreviouslyStoredProjectCollectionsRemainReadable(string payload)
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder => builder.AddAssembly(typeof(SolutionMap).Assembly));
        using var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var value = Assert.IsType<SolutionMap>(serializer.Deserialize<object>(Convert.FromBase64String(payload)));
        Assert.Equal("legacy.slnx", value.SolutionPath);
        Assert.Equal(new ProjectNode("Sample", "Sample.csproj", "test", 3), Assert.Single(value.Projects));
        Assert.Equal(new ProjectEdge("Sample", "Dependency"), Assert.Single(value.References));
        var reloaded = serializer.Deserialize<SolutionMap>(serializer.SerializeToArray(value));
        Assert.NotNull(reloaded);
        Assert.Equal(value.Projects, reloaded.Projects);
        Assert.Equal(value.References, reloaded.References);
    }
}
