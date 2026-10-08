using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chat;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

public sealed class ChatMigrationFacts
{
    [Fact]
    public async Task AStoredProjectionWithoutActionsRemainsReadableAndCanAdvance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<FlutterModule>()
            .ConfigureSilo(silo =>
            {
                silo.AddMemoryGrainStorage("legacy-chat");
                silo.Services.AddKeyedSingleton<IGrainStorage>("Default", (services, _) =>
                    new LegacyStorage(services.GetRequiredKeyedService<IGrainStorage>("legacy-chat")));
            }).StartAsync(ct);
        var chat = brain.Get<IChat>("legacy");
        var old = await chat.Read();
        Assert.Equal("saved answer", Assert.Single(old.Projection!.Messages).Text);
        Assert.NotNull(old.Projection.Actions);
        Assert.Empty(old.Projection.Actions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Activate("conversation", 1, "missing"));
        await chat.Project(old.Projection with { Version = 2, Actions = [new("run-2", "Stop", "two")] });
        await brain.DeactivateAsync(chat, ct);
        Assert.Equal("run-2", Assert.Single((await chat.Read()).Projection!.Actions).Id);
    }

    private sealed class LegacyStorage(IGrainStorage inner) : IGrainStorage
    {
        private bool seeded;
        public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
        {
            await inner.ReadStateAsync(stateName, grainId, state);
            if (typeof(T) == typeof(ChatState) && !seeded)
            {
                seeded = true;
                // A field absent from an older Orleans payload has its zero value.
                state.State = (T)(object)new ChatState
                {
                    Name = "legacy",
                    Revision = 1,
                    Projection = new("conversation", 1, 1, [new("reply", ChatRole.Assistant, "saved answer")]) { Actions = null! },
                };
            }
        }
        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
            => inner.WriteStateAsync(stateName, grainId, state);
        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> state)
            => inner.ClearStateAsync(stateName, grainId, state);
    }
}
