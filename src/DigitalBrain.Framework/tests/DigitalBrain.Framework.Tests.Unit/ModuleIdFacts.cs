namespace DigitalBrain.Framework.Tests.Unit;

public sealed class ModuleIdFacts
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("ai")]
    [InlineData("csharp-authoring")]
    public void A_valid_id_round_trips_as_its_string(string value)
        => Assert.Equal(value, new DigitalBrain.ModuleId(value).ToString());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("a_b")]
    [InlineData("a:b")]
    public void An_id_outside_the_grammar_cannot_be_constructed(string value)
        => Assert.ThrowsAny<ArgumentException>(() => new DigitalBrain.ModuleId(value));

    [Fact]
    public void Ids_differing_only_by_case_are_the_same_id()
        => Assert.Equal(new DigitalBrain.ModuleId("AI"), new DigitalBrain.ModuleId("ai"));

    [Fact]
    public void The_configuration_sections_speak_module_id()
        => Assert.Equal("DigitalBrain:Modules:ai:Options",
            DigitalBrain.DigitalBrainConfiguration.ModuleOptionsSection(new DigitalBrain.ModuleId("ai")));
}
