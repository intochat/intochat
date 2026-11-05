using System.Xml.Linq;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class PinCoherenceFacts
{
    [Fact]
    public void PinsAreCoherentPerFamily()
    {
        var props = XDocument.Load(Path.Combine(Repository.Root, "Directory.Packages.props"));
        var pins = props.Descendants("PackageVersion")
            .Select(element => (Id: element.Attribute("Include")!.Value, Version: element.Attribute("Version")!.Value))
            .ToArray();

        var digitalBrainVersions = pins
            .Where(pin => pin.Id == "DigitalBrain" || pin.Id.StartsWith("DigitalBrain.", StringComparison.Ordinal))
            .Select(pin => pin.Version)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(digitalBrainVersions);

        var intoChatPins = pins
            .Where(pin => pin.Id.StartsWith("IntoChat.", StringComparison.Ordinal) || pin.Id.StartsWith("IntoChat.OS.Modules.", StringComparison.Ordinal))
            .Select(pin => pin.Id)
            .ToArray();
        Assert.Empty(intoChatPins);
    }

    [Fact]
    public void Sources_are_remote_only()
    {
        var directory = new DirectoryInfo(Repository.Root);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "nuget.config"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        var config = XDocument.Load(Path.Combine(directory.FullName, "nuget.config"));
        var sources = config.Descendants("packageSources").Elements("add").Select(e => e.Attribute("value")!.Value).ToList();

        Assert.NotEmpty(sources);
        Assert.NotEmpty(config.Descendants("packageSources").Elements("clear"));
        Assert.All(sources, value =>
        {
            Assert.StartsWith("https://", value, StringComparison.Ordinal);
            Assert.DoesNotContain("local-feed", value, StringComparison.OrdinalIgnoreCase);
        });
        Assert.NotEmpty(config.Descendants("packageSourceMapping"));
    }
}
