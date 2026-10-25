using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.Integration;

public sealed class ModuleIdFacts
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("ai")]
    [InlineData("csharp-authoring")]
    public void A_valid_id_round_trips_as_its_string(string value)
        => Assert.Equal(value, new IntoChat.OS.Sdk.Modules.ModuleId(value).ToString());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("a:b")]
    public void An_id_outside_the_grammar_cannot_be_constructed(string value)
        => Assert.ThrowsAny<ArgumentException>(() => new IntoChat.OS.Sdk.Modules.ModuleId(value));

    [Fact]
    public void Ids_differing_only_by_case_are_the_same_id()
        => Assert.Equal(new IntoChat.OS.Sdk.Modules.ModuleId("AI"), new IntoChat.OS.Sdk.Modules.ModuleId("ai"));

    [Fact]
    public void The_configuration_sections_speak_module_id()
        => Assert.Equal("IntoChatOS:Modules:ai:Options",
            IntoChat.OS.Sdk.Configuration.IntoChatOSConfiguration.ModuleOptionsSection(new IntoChat.OS.Sdk.Modules.ModuleId("ai")));
}
