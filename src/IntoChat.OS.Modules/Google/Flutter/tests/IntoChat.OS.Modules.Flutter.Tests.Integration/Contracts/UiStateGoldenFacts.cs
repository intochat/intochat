using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Contracts;

// W2 PR 0 (ui-contract 7.1): the JSON the shell reads over GET /apps/node (T3) and posts to /apps/event (T4) is
// pinned per record: every state a UI contract's Read returns, the view tree (UiView/UiNode) and UiEvent. A
// renamed, retyped or removed member fails here; an added member is a reviewed golden update through
// CaptureUiStateGoldens (run explicitly).
public sealed class UiStateGoldenFacts(FlutterHost host)
{
    internal const string GoldenFile = "ui-states.golden.json";

    // The options the endpoints really use: the view's own (AppUiEndpoints.ViewJson) and, for every other kind and
    // for the /event body, the running host's HTTP JSON options, so a settings change fails the pin.
    private JsonSerializerOptions Wire(Type type) => type == typeof(UiView) || type == typeof(UiNode)
        ? AppUiEndpoints.ViewJson
        : host.Brain.Services.GetRequiredService<IOptions<global::Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

    internal static Type[] Records()
    {
        var states = typeof(UIVocabulary).Assembly.GetExportedTypes()
            .Where(type => type.IsInterface && (typeof(INeuron).IsAssignableFrom(type) || IntoChat.OS.Runtime.PlainNeuronContracts.Is(type)))
            .SelectMany(contract => contract.GetMethods())
            .Where(method => method.Name == "Read" && method.GetParameters().Length == 0 && method.ReturnType.IsGenericType)
            .Select(method => method.ReturnType.GetGenericArguments()[0]);
        return [.. states.Append(typeof(UiView)).Append(typeof(UiNode)).Append(typeof(AppUiEndpoints.UiEvent)).Distinct().OrderBy(type => type.FullName, StringComparer.Ordinal)];
    }

    private JsonNode Serialize(Type type, object value) => JsonNode.Parse(JsonSerializer.Serialize(value, type, Wire(type)))!;

    [Fact]
    public void EveryUiRecordHasAGolden()
    {
        var golden = UiContractFiles.Golden(GoldenFile);

        Assert.Equal(golden.Select(entry => entry.Key).Order(StringComparer.Ordinal), Records().Select(type => type.FullName!));
    }

    public static TheoryData<string> RecordNames() => [.. Records().Select(type => type.FullName!)];

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void TheRecordWritesItsGolden(string record)
    {
        var type = Records().Single(type => type.FullName == record);
        var golden = UiContractFiles.Golden(GoldenFile)[record]!;
        var written = Serialize(type, UiSamples.Of(type));

        Assert.True(JsonNode.DeepEquals(golden, written), $"{record} no longer writes its golden JSON:\n{written.ToJsonString()}\nexpected:\n{golden.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(RecordNames))]
    public void TheGoldenReadsBackUnchanged(string record)
    {
        var type = Records().Single(type => type.FullName == record);
        var golden = UiContractFiles.Golden(GoldenFile)[record]!;
        var read = JsonSerializer.Deserialize(golden.ToJsonString(), type, Wire(type))!;

        Assert.True(JsonNode.DeepEquals(golden, Serialize(type, read)), $"{record} does not read its golden JSON back unchanged.");
    }

    [Fact(Explicit = true)]
    public void CaptureUiStateGoldens()
    {
        var all = new JsonObject();
        foreach (var type in Records()) { all[type.FullName!] = Serialize(type, UiSamples.Of(type)); }
        UiContractFiles.Write(Path.Combine("Contracts", GoldenFile), all);
    }
}
