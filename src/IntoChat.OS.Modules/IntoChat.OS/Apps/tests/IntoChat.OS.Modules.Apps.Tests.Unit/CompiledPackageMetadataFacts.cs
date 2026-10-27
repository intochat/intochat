using System.Collections.Immutable;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class CompiledPackageMetadataFacts
{
    [Fact]
    public void FrameworkOnlyPackageSuppliesInertOperationsAndCapabilityNeeds()
    {
        var manifest = CompiledPackageMetadata.Manifest(Descriptor());
        Assert.Equal("Counter", manifest.Title);
        Assert.Equal(["open", "increment"], manifest.Operations.Select(operation => operation.Name));
        Assert.Equal(["storage@1"], manifest.Requires!.Capabilities!);
    }

    [Fact]
    public void OsExtensionRetainsSettingsSlotsToolbarAndAdditionalRequirements()
    {
        var expected = new AppManifest("Counter", "Stored counts", [new("open", "Open")], [new("color", "Color", "blue")],
            [new("documents", Capability: "storage@1")], "icon.svg", [new("open", Label: "Open")],
            new(SemanticTypes: ["person.name"], Secrets: ["service.key"], Capabilities: ["storage@1"]));
        var actual = CompiledPackageMetadata.Manifest(WithExtension(expected));
        Assert.Equal(expected.Settings, actual.Settings);
        Assert.Equal(expected.Slots, actual.Slots);
        Assert.Equal(expected.Toolbar, actual.Toolbar);
        Assert.Equal(expected.Requires, actual.Requires);
    }

    [Theory]
    [InlineData("missing-capability")]
    [InlineData("added-capability")]
    [InlineData("unknown-operation")]
    [InlineData("duplicate-operation")]
    [InlineData("null-arrays")]
    [InlineData("duplicate-json")]
    [InlineData("unknown-json")]
    [InlineData("invalid-json")]
    public void ExtensionCannotConcealOrInventTheExecutableContract(string changed)
    {
        var manifest = new AppManifest("Counter", "Counts", [new("open", "Open")], [], [], Requires: new(Capabilities: ["storage@1"]));
        var descriptor = changed switch
        {
            "missing-capability" => WithExtension(manifest with { Requires = new() }),
            "added-capability" => WithExtension(manifest with { Requires = new(Capabilities: ["storage@1", "identity@1"]) }),
            "unknown-operation" => WithExtension(manifest with { Operations = [new("other", "Other")] }),
            "duplicate-operation" => WithExtension(manifest with { Operations = [new("open", "Open"), new("open", "Again")] }),
            "null-arrays" => WithExtension(manifest with { Operations = null! }),
            "duplicate-json" => WithJson("{\"title\":\"A\",\"title\":\"B\"}"),
            "unknown-json" => WithJson("{\"title\":\"A\",\"unknown\":true}"),
            "invalid-json" => WithJson("invalid"),
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };
        Assert.Throws<InvalidDataException>(() => CompiledPackageMetadata.Manifest(descriptor));
    }

    private static CompiledAppDescriptor WithExtension(AppManifest manifest)
        => WithJson(JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static CompiledAppDescriptor WithJson(string json)
        => Descriptor() with { HostMetadata = ImmutableSortedDictionary<string, string>.Empty.Add("intochat.os.v1", json) };

    private static CompiledAppDescriptor Descriptor() => new()
    {
        Id = "counter", Name = "Counter", Version = "1", EntryAssembly = "Counter.dll", EntryType = "CounterApp",
        Capabilities = ["storage@1"],
        Presentation = ImmutableSortedDictionary<string, string>.Empty.Add("operations", "[\"open\",\"increment\"]"),
    };
}
