using System.Text.Json;
using IntoChat.OS.Modules.Flame.Scene;
using Xunit;

namespace IntoChat.OS.Modules.Flame.Tests.Integration.Schema;

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

        Assert.Equal(TreeKinds.All, Strings(root.GetProperty("treeKinds")));
        Assert.Equal(FlameSchema.ReservedBlackboardKeys, Strings(root.GetProperty("reservedBlackboardKeys")));
        Assert.Equal(FlameVocabulary.MaxTreeNodes, root.GetProperty("maxTreeNodes").GetInt32());
        Assert.Equal(FlameVocabulary.MaxTreeDepth, root.GetProperty("maxTreeDepth").GetInt32());
        var tasks = root.GetProperty("treeTasks").EnumerateArray().ToArray();
        Assert.Equal(FlameSchema.Tasks.Select(task => task.Kind).ToArray(), tasks.Select(task => task.GetProperty("kind").GetString()!).ToArray());
        for (var index = 0; index < FlameSchema.Tasks.Length; index++)
        {
            var task = FlameSchema.Tasks[index];
            Assert.Equal(task.MinSeconds, tasks[index].GetProperty("minSeconds").GetDouble());
            Assert.Equal(task.MaxSeconds, tasks[index].GetProperty("maxSeconds").GetDouble());
            Assert.Equal(task.MinValue, tasks[index].GetProperty("minValue").GetDouble());
            Assert.Equal(task.MaxValue, tasks[index].GetProperty("maxValue").GetDouble());
        }

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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Intochat.OS.slnx"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
