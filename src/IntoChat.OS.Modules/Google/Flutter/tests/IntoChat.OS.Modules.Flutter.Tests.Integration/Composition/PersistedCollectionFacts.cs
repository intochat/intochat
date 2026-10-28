using DigitalBrain;
using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Collection;
using IntoChat.OS.Modules.Flutter.ImageCanvas;
using IntoChat.OS.Modules.Flutter.Layout;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class PersistedCollectionFacts(FlutterHost host)
{
    [Fact]
    public async Task BreadcrumbsSurviveReactivationAndRejectUnknownOrStaleNavigation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var collection = brain.Get<ICollectionView>("collections/navigation");
        await collection.Set(new([], Breadcrumbs: [new("storages", "Storages"), new("root", "Documents")]), 0);
        await brain.DeactivateAsync(collection, ct);
        Assert.Equal(["storages", "root"], (await collection.Read()).Definition.Breadcrumbs!.Select(x => x.Id));
        await collection.Activate("storages", 1);
        await Assert.ThrowsAsync<ArgumentException>(() => collection.Activate("outside", 1));
        await Assert.ThrowsAsync<RevisionConflictException>(() => collection.Activate("storages", 0));
    }

    [Fact]
    public async Task CollectionAndLayoutPreserveOrderAndExtentsAfterReactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var collection = brain.Get<ICollectionView>("collections/items");
        await collection.Set(new([new("b", "Second", "file"), new("a", "First", "file")], Cursor: "next"), 0);
        await brain.DeactivateAsync(collection, ct);
        var restored = (await collection.Read()).Definition;
        Assert.Equal(["b", "a"], restored.Items.Select(item => item.Id));
        Assert.Equal("next", restored.Cursor);
        var layout = brain.Get<ILayout>("collections/layout");
        await layout.Set(new("column", [new("collection", "collections/items"), new("text", "collections/status")], Extents: [0, 48]), 0);
        await brain.DeactivateAsync(layout, ct);
        var extents = (await layout.Read()).Definition.Extents;
        Assert.NotNull(extents);
        Assert.Equal([0d, 48d], extents);
        await layout.Set(new("row", []), 1);
        await brain.DeactivateAsync(layout, ct);
        Assert.Null((await layout.Read()).Definition.Extents);
    }

    [Fact]
    public void ArrayFieldsRetainTheCamelCaseJsonShapeAndNullableExtents()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string json = """{"mode":"column","children":[{"kind":"text","name":"status"}],"gap":12,"extents":null}""";
        var layout = JsonSerializer.Deserialize<LayoutDefinition>(json, options)!;
        Assert.Equal("status", Assert.Single(layout.Children).Name);
        Assert.Null(layout.Extents);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(layout, options));
        Assert.Equal(JsonValueKind.Array, serialized.RootElement.GetProperty("children").ValueKind);
        Assert.Equal(JsonValueKind.Null, serialized.RootElement.GetProperty("extents").ValueKind);
        const string recipeJson = """{"crop":null,"strokes":[{"colorArgb":4294901760,"width":3,"points":[{"x":20,"y":30}]}]}""";
        var recipe = JsonSerializer.Deserialize<ImageRecipe>(recipeJson, options)!;
        Assert.Equal(new ImagePoint(20, 30), Assert.Single(Assert.Single(recipe.Strokes).Points));
        Assert.Equal(recipeJson, JsonSerializer.Serialize(recipe, options));
    }
}
