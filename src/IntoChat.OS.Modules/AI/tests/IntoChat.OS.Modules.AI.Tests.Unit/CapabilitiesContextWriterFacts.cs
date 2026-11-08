using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Routing;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class CapabilitiesContextWriterFacts
{
    private sealed class FakeContext : IAgentContext
    {
        public Dictionary<string, ContextEntry> Held { get; } = new(StringComparer.Ordinal);
        public Task Set(ContextEntry entry) { Held[entry.Name] = entry; return Task.CompletedTask; }
        public Task Remove(string name) { Held.Remove(name); return Task.CompletedTask; }
        public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(0, [.. Held.Values]));
    }

    private static (string App, MenuOperation[] Operations) Notes() => ("notes", [
        new MenuOperation("notes", "add", "Add a note"),
        new MenuOperation("notes", "list", null)]);

    [Fact]
    public void AnAppBecomesOneEntryWithALinePerOperation()
    {
        var entry = Assert.Single(CapabilitiesContextWriter.Entries([Notes(), ("empty", [])]));
        Assert.Equal("notes", entry.Name);
        Assert.Equal("notes.add: Add a note\nnotes.list", entry.Text);
        Assert.Equal([typeof(IntoChat.OS.Modules.Apps.IApp).FullName!], entry.Contracts);
    }

    [Fact]
    public async Task AReplaceDropsTheAppsThatLeft()
    {
        var context = new FakeContext();
        await CapabilitiesContextWriter.Replace(context, [Notes(), ("tasks", [new MenuOperation("tasks", "run", "Run it")])]);
        Assert.Equal(["notes", "tasks"], context.Held.Keys.Order());

        await CapabilitiesContextWriter.Replace(context, [Notes()]);
        Assert.Equal(["notes"], context.Held.Keys);
    }

    [Fact]
    public async Task AnUpsertRewritesOneAppAndNoOperationsRemovesIt()
    {
        var context = new FakeContext();
        await CapabilitiesContextWriter.Upsert(context, "notes", Notes().Operations);
        await CapabilitiesContextWriter.Upsert(context, "notes", [new MenuOperation("notes", "add", "Add one")]);
        Assert.Equal("notes.add: Add one", context.Held["notes"].Text);

        await CapabilitiesContextWriter.Upsert(context, "notes", []);
        Assert.Empty(context.Held);
    }

    private static ContextEntry Interrupt(string name, string text, string trust)
        => new(name, text, [], [ContextTags.Interrupt], new EntryProvenance("writer", DateTimeOffset.UtcNow, trust));

    [Fact]
    public async Task AnAppNeverReplacesOrDropsAUserOrPlatformInterruptEntry()
    {
        var context = new FakeContext();
        await context.Set(Interrupt("stop", "halt", ContextTrust.User));
        await context.Set(Interrupt("halt", "also halt", ContextTrust.Platform));

        await CapabilitiesContextWriter.Upsert(context, "stop", Notes().Operations);
        await CapabilitiesContextWriter.Upsert(context, "stop", []);
        await CapabilitiesContextWriter.Replace(context, [Notes(), ("stop", [new MenuOperation("stop", "run", null)])]);

        Assert.Equal("halt", context.Held["stop"].Text);
        Assert.True(context.Held.ContainsKey("halt"));
        Assert.True(context.Held.ContainsKey("notes"));
    }

    [Fact]
    public async Task AnAppReplacesAModelPlantedInterruptEntry()
    {
        var context = new FakeContext();
        await context.Set(Interrupt("notes", "planted", ContextTrust.Model));
        await context.Set(Interrupt("gone", "planted too", ContextTrust.App));

        await CapabilitiesContextWriter.Replace(context, [Notes()]);

        Assert.Equal("notes.add: Add a note\nnotes.list", context.Held["notes"].Text);
        Assert.False(context.Held.ContainsKey("gone"));
    }
}
