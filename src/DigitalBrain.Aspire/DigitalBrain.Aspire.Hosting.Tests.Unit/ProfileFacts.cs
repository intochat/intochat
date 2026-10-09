using DigitalBrain.Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Aspire.Hosting.Tests.Unit;

public sealed class ProfileFacts : IDisposable
{
    private readonly string _appHost = Directory.CreateTempSubdirectory("profiles-").FullName;

    public ProfileFacts()
    {
        Directory.CreateDirectory(Path.Combine(_appHost, DigitalBrainProfiles.Directory));
        Write("dev", """{}""");
        Write("local", """{ "Model": "local", "Voice": "local" }""");
        Write("browser", """{ "Model": "browser-wins", "Browser": "container" }""");
    }

    [Fact]
    public void WithoutAProfileTheDevPresetIsLoaded()
    {
        var configuration = new ConfigurationBuilder();
        Assert.Equal(["dev"], configuration.AddDigitalBrainProfiles(_appHost));
    }

    [Fact]
    public void ComposedProfilesLayerLeftToRight()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DigitalBrainProfiles.ProfileKey] = "local+browser" });
        configuration.AddDigitalBrainProfiles(_appHost);
        var built = configuration.Build();
        Assert.Equal("browser-wins", built["Model"]);
        Assert.Equal("local", built["Voice"]);
        Assert.Equal("container", built["Browser"]);
    }

    [Fact]
    public void AnExplicitSettingOutranksTheProfile()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(_appHost, DigitalBrainProfiles.Directory, "dev.json"))
            .AddInMemoryCollection(new Dictionary<string, string?> { [DigitalBrainProfiles.ProfileKey] = "local", ["Voice"] = "cloud" });
        configuration.AddDigitalBrainProfiles(_appHost);
        Assert.Equal("cloud", configuration.Build()["Voice"]);
    }

    [Fact]
    public void AnUnknownProfileNamesTheKnownOnes()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [DigitalBrainProfiles.ProfileKey] = "cloud" });
        var error = Assert.Throws<InvalidOperationException>(() => configuration.AddDigitalBrainProfiles(_appHost));
        Assert.Contains("browser, dev, local", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_appHost, recursive: true);

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_appHost, DigitalBrainProfiles.Directory, name + ".json"), json);
}
