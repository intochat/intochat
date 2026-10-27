using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IntoChat.OS.Modules.AI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Inference;

public sealed class AiMessageCompatibilityFacts(AIHost host)
{
    [Fact]
    public void ExistingCompiledConsumersCanResolveTheReadOnlyListSurface()
    {
        var message = typeof(AiMessage);
        var content = typeof(IReadOnlyList<AiContent>);
        Assert.NotNull(message.GetConstructor([typeof(string), content]));
        var property = message.GetProperty(nameof(AiMessage.Content))!;
        Assert.Equal(content, property.GetMethod!.ReturnType);
        Assert.Equal(content, Assert.Single(property.SetMethod!.GetParameters()).ParameterType);
        Assert.Contains(typeof(IsExternalInit), property.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        Assert.NotNull(message.GetMethod("Deconstruct", [typeof(string).MakeByRefType(), content.MakeByRefType()]));
    }

    [Fact]
    public void ExistingSourceConsumersProduceDurableContentForEveryAssignment()
    {
        var list = new List<AiContent> { new AiText("list") };
        IReadOnlyList<AiContent> expression = [new AiText("expression")];
        var fromList = new AiMessage("user", list);
        var fromInterface = new AiMessage("user", expression);
        var fromExpression = new AiMessage("user", [new AiText("direct")]);
        var replaced = fromList with { Content = expression };
        var initialized = new AiMessage("user", []) { Content = list };
        list.Clear();

        Assert.Equal("list", Assert.IsType<AiText>(Assert.Single(Assert.IsType<AiContent[]>(fromList.Content))).Text);
        Assert.Equal("list", Assert.IsType<AiText>(Assert.Single(Assert.IsType<AiContent[]>(initialized.Content))).Text);
        Assert.Equal("expression", Assert.IsType<AiText>(Assert.Single(Assert.IsType<AiContent[]>(fromInterface.Content))).Text);
        Assert.Equal("direct", Assert.IsType<AiText>(Assert.Single(Assert.IsType<AiContent[]>(fromExpression.Content))).Text);
        replaced.Deconstruct(out string role, out IReadOnlyList<AiContent> content);
        Assert.Equal("user", role);
        Assert.Equal("expression", Assert.IsType<AiText>(Assert.Single(Assert.IsType<AiContent[]>(content))).Text);
    }

    [Fact]
    public void ExistingJsonPayloadRestoresConcretePolymorphicContent()
    {
        const string payload = """{"Role":"assistant","Content":[{"$content":"text","Text":"checking"},{"$content":"tool-call","CallId":"call-1","Name":"lookup","ArgumentsJson":"{}"}]}""";
        var message = JsonSerializer.Deserialize<AiMessage>(payload)!;
        var restored = JsonSerializer.Deserialize<AiMessage>(JsonSerializer.Serialize(message))!;
        Assert.IsType<AiContent[]>(message.Content);
        Assert.IsType<AiContent[]>(restored.Content);
        Assert.Equal("checking", Assert.IsType<AiText>(restored.Content[0]).Text);
        Assert.Equal(new AiToolCall("call-1", "lookup", "{}"), Assert.IsType<AiToolCall>(restored.Content[1]));
    }

    [Fact]
    public async Task NormalizedContentSurvivesOrleansSerialization()
    {
        var brain = host.Brain;
        var serializer = brain.Services.GetRequiredService<Serializer<AiMessage>>();
        IReadOnlyList<AiContent> content = [new AiText("hello"), new AiToolResult("call-1", "{}")];
        var restored = serializer.Deserialize(serializer.SerializeToArray(new AiMessage("assistant", content)));
        Assert.NotNull(restored);
        Assert.Equal("assistant", restored.Role);
        Assert.IsType<AiContent[]>(restored.Content);
        Assert.Equal(new AiText("hello"), restored.Content[0]);
        Assert.Equal(new AiToolResult("call-1", "{}"), restored.Content[1]);
    }

    [Fact]
    public async Task LegacyListContentWirePayloadRemainsReadable()
    {
        var brain = host.Brain;
        var serializer = brain.Services.GetRequiredService<Serializer<AiMessage>>();
        var legacy = new AiMessage("assistant", []);
        // The old positional record stored the caller's list without normalization. Recreate
        // that object shape so this exercises the legacy list codec under the unchanged Id(1).
        var contentField = Assert.Single(typeof(AiMessage).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(IReadOnlyList<AiContent>));
        contentField.SetValue(legacy, new List<AiContent> { new AiText("legacy"), new AiToolResult("old-call", "{}") });
        var payload = serializer.SerializeToArray(legacy);

        var restored = serializer.Deserialize(payload);
        Assert.NotNull(restored);
        Assert.Equal("assistant", restored.Role);
        Assert.Equal(new AiText("legacy"), restored.Content[0]);
        Assert.Equal(new AiToolResult("old-call", "{}"), restored.Content[1]);
        var persisted = JsonSerializer.Deserialize<AiMessage>(JsonSerializer.Serialize(restored))!;
        Assert.IsType<AiContent[]>(persisted.Content);
        Assert.Equal(restored.Content, persisted.Content);
    }
}
