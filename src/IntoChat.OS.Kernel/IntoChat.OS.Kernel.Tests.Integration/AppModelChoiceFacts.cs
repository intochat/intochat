using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Connections.Platform.Entries;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.Integration;

// Engineer and Ino validate a Model setting through the catalog each addresses. A model unlocked only by
// a brain's own provider key is a valid choice in that brain and refused in another.
public sealed class AppModelChoiceFacts
{
    private const string Keyed = "app-choice-keyed";
    private const string Other = "app-choice-other";

    [Trait("host", "own")]
    [Fact]
    public Task Engineer_accepts_a_brain_key_model_only_in_that_brain() => Proves(Engineer.ModelCatalogKey);

    [Trait("host", "own")]
    [Fact]
    public Task Ino_accepts_a_brain_key_model_only_in_that_brain() => Proves(Ino.ModelCatalogKey);

    private static async Task Proves(Func<string, string> catalogKey)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AIModule>().StartAsync(ct);
        await brain.Services.GetRequiredService<IntoChat.OS.Runtime.IStillGrains>().Get<IBrainEntries>(Keyed)
            .Put(BrainEntries.ProviderEntry("openai"), EntryKind.Secret, "brain-only-key-8c21");

        Assert.Equal(new ModelSelection("IGpt54Nano"), await brain.Get<IModelCatalog>(catalogKey(Keyed)).Select("preset:IGpt54Nano"));
        await Assert.ThrowsAnyAsync<Exception>(() => brain.Get<IModelCatalog>(catalogKey(Other)).Select("preset:IGpt54Nano"));
    }
}
