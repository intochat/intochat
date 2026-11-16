using System.Reflection;
using DigitalBrain;
using IntoChat.OS.Connections;
using IntoChat.OS.Primitives;

namespace IntoChat.OS.Tests.Unit.Primitives;

public sealed class AppRefAndUnitAliasFacts
{
    [Fact]
    public void PrimitivesHaveNoPublicStringConstructorAndNoImplicitStringConversion()
    {
        foreach (var type in new[] { typeof(AppRef), typeof(UnitAlias) })
        {
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            var implicitOps = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "op_Implicit" && m.ReturnType == typeof(string));
            Assert.Empty(implicitOps);
        }
    }

    [Fact]
    public void AppRefBuildsKeysAndNeuronIdsCleanly()
    {
        var brain = new BrainId("owner");
        var app = AppRef.Of(brain, "notes");

        Assert.Equal(brain, app.Brain);
        Assert.Equal("notes", app.Name);
        Assert.Equal("owner/apps/notes", app.Key);
        Assert.Equal("owner/apps/notes/view", app.ViewKey);
        Assert.Equal("apps/notes", app.RelativePath);
        Assert.Equal("apps/notes/view", app.ViewPath);
        Assert.Equal("owner/apps/notes/data", app.ChildKey("data"));
        Assert.Equal(new NeuronId(brain, AppGrainType.Name, "apps/notes"), app.Neuron);
        Assert.Equal(new NeuronId(brain, "app.notes", "default"), app.ScreenNeuron);
        Assert.Equal(new NeuronId(brain, "apps.data", "apps/notes/items"), app.ChildNeuron("apps.data", "items"));
        Assert.Equal($"{AppGrainType.Name}/owner/apps/*", AppRef.WildcardTarget(brain));
    }

    [Theory]
    [InlineData("owner/apps/notes", "owner", "notes")]
    [InlineData("brain-alpha.1/apps/my_tool-v2", "brain-alpha.1", "my_tool-v2")]
    public void AppRefParseAndTryParseAcceptValidKeys(string raw, string expectedBrain, string expectedName)
    {
        var parsed = AppRef.Parse(raw);
        Assert.Equal(new BrainId(expectedBrain), parsed.Brain);
        Assert.Equal(expectedName, parsed.Name);
        Assert.True(AppRef.TryParse(new BrainId(expectedBrain), raw, out var scoped));
        Assert.Equal(parsed, scoped);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owner/notes")]
    [InlineData("owner/apps/")]
    [InlineData("/apps/notes")]
    [InlineData("owner/apps/notes/extra")]
    [InlineData("owner/apps/..")]
    [InlineData("owner/apps/bad..name")]
    [InlineData("brain:invalid/apps/notes")]
    public void AppRefTryParseRejectsMalformedKeys(string? raw)
    {
        Assert.False(AppRef.TryParse(raw, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("foo..bar")]
    [InlineData("bad\nname")]
    public void AppRefOfRejectsUnsafeAppNames(string badName)
    {
        Assert.False(AppRef.TryOf(new BrainId("owner"), badName, out _));
        Assert.ThrowsAny<ArgumentException>(() => AppRef.Of(new BrainId("owner"), badName));
    }

    [Fact]
    public void AppRefOfRejectsDefaultBrainId()
    {
        Assert.False(AppRef.TryOf(default, "notes", out _));
        Assert.Throws<ArgumentException>(() => AppRef.Of(default, "notes"));
    }

    [Theory]
    [InlineData("ino")]
    [InlineData("chat")]
    [InlineData("my-app_1.v2")]
    public void UnitAliasAcceptsValidAliasesAndBuildsNeuronIds(string raw)
    {
        var alias = UnitAlias.Parse(raw);
        Assert.Equal(raw, alias.Value);

        var brain = new BrainId("owner");
        Assert.Equal(new NeuronId(brain, raw, "default"), alias.StateNeuron(brain));
        Assert.Equal(new NeuronId(brain, "app." + raw, "default"), alias.ScreenNeuron(brain));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UpperCase")]
    [InlineData("has/slash")]
    [InlineData(@"has\slash")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("foo..bar")]
    [InlineData(".leading")]
    [InlineData("trailing.")]
    [InlineData("apps")]
    [InlineData("images")]
    [InlineData("applications")]
    [InlineData("units")]
    public void UnitAliasRejectsInvalidOrReservedNames(string? bad)
    {
        Assert.False(UnitAlias.TryParse(bad, out _));
    }

    [Theory]
    [InlineData("ino", "ino")]
    [InlineData("owner/ino", "ino")]
    [InlineData("owner/units/ino", "ino")]
    [InlineData("owner/ino/view", "ino")]
    [InlineData("owner/units/ino/view", "ino")]
    public void UnitAliasTryFromAddressParsesCanonicalForms(string address, string expected)
    {
        Assert.True(UnitAlias.TryFromAddress(new BrainId("owner"), address, out var alias));
        Assert.Equal(expected, alias.Value);
    }

    [Theory]
    [InlineData("other/ino")]
    [InlineData("owner/apps/ino")]
    [InlineData("owner/ino/extra")]
    [InlineData("owner/units/ino/side")]
    public void UnitAliasTryFromAddressRejectsMismatchedOrNonUnitPaths(string address)
    {
        Assert.False(UnitAlias.TryFromAddress(new BrainId("owner"), address, out _));
    }
}
