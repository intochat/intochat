using DigitalBrain.Flame;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Schema;

public sealed class FlameSchemaFacts
{
    [Fact]
    public void TheSchemaCoversExactlyTheVocabularyNodeTypes()
        => Assert.Equal(FlameVocabulary.NodeTypes, FlameSchema.Nodes.Select(node => node.Type).ToArray());

    [Fact]
    public void EveryChoiceParamNamesItsDomain()
    {
        foreach (var node in FlameSchema.Nodes)
        {
            foreach (var param in node.Parameters.Where(param => param.Kind == FlameParamKind.Choice))
            {
                Assert.NotNull(param.Choices);
                Assert.NotEmpty(param.Choices!);
            }
        }
    }

    [Fact]
    public void EveryParameterKeyIsUniqueWithinItsType()
    {
        foreach (var node in FlameSchema.Nodes)
        {
            Assert.Equal(node.Parameters.Length, node.Parameters.Select(param => param.Key).Distinct(StringComparer.Ordinal).Count());
        }
    }
}
