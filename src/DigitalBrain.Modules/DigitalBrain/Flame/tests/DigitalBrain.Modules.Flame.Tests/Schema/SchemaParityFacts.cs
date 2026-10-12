using System.Text.Json;
using DigitalBrain.Flame;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Schema;

// The one schema both languages read: docs/superpowers/contracts/flame-schema.json. This fails if the C#
// FlameSchema drifts from it; node_schema_test.dart fails if the Dart mirror drifts from the same file.
public sealed class SchemaParityFacts
{
    [Fact]
    public void TheCheckedInSchemaMatchesTheContracts()
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs/superpowers/contracts/flame-schema.json"))).RootElement;
        Assert.Equal(FlameSchema.Gestures, Strings(root.GetProperty("gestures")));
        Assert.Equal(FlameSchema.TweenProperties, Strings(root.GetProperty("tweenProperties")));
        Assert.Equal(FlameSchema.TweenCurves, Strings(root.GetProperty("tweenCurves")));

        var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(FlameSchema.Nodes.Select(node => node.Type).ToArray(), nodes.Select(node => node.GetProperty("type").GetString()!).ToArray());
        foreach (var node in FlameSchema.Nodes)
        {
            var json = nodes.Single(candidate => candidate.GetProperty("type").GetString() == node.Type);
            var parameters = json.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.Equal(node.Parameters.Select(param => param.Key).ToArray(), parameters.Select(param => param.GetProperty("key").GetString()!).ToArray());
            for (var index = 0; index < node.Parameters.Length; index++)
            {
                Assert.Equal(node.Parameters[index].Kind.ToString().ToLowerInvariant(), parameters[index].GetProperty("kind").GetString());
                Assert.Equal(node.Parameters[index].Required, parameters[index].GetProperty("required").GetBoolean());
                Assert.Equal(node.Parameters[index].Choices ?? [], Strings(parameters[index].GetProperty("choices")));
            }
        }
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(element => element.GetString()!)];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
