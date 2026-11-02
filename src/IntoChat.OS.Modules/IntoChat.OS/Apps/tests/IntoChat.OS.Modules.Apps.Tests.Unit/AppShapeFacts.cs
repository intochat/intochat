using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppShapeFacts
{
    private const string Main = """
        using DigitalBrain;
        [App("habits")]
        public sealed partial class HabitsApp : App<HabitsState>
        {
            // Show today's habits.
            [On] public void Open() { }
            [DigitalBrain.On] public Task CheckIn(string habit) => Task.CompletedTask;
            [On("habits/source", Contract = typeof(IHabits))] public void OnChanged(HabitChanged changed) { }
            public void Helper() { }
        }
        """;

    private const string Synapse = """
        public sealed partial class HabitsApp
        {
            [On] public void HotRestart() { }
        }
        """;

    [Fact]
    public void Operations_AreTheOnMethodsAcrossPartialFiles_WithoutReactions()
    {
        var operations = AppShape.Operations(new Dictionary<string, string> { ["main.cs"] = Main, ["on-restart.cs"] = Synapse, ["notes.md"] = "[On] nope" });

        Assert.Equal(["open", "check-in", "hot-restart"], operations.Select(operation => operation.Name));
        Assert.Equal("Show today's habits.", operations[0].Description);
    }

    [Fact]
    public void Merge_KeepsDeclaredDescriptions_OnlyForOperationsTheCodeHas()
    {
        var derived = AppShape.Operations(new Dictionary<string, string> { ["main.cs"] = Main });

        var merged = AppShape.Merge(derived, [new("checkin", "Mark a habit done."), new("delete", "Not in code.")]);

        Assert.Equal(["open", "check-in"], merged.Select(operation => operation.Name));
        Assert.Equal("Mark a habit done.", merged[1].Description);
    }

    [Fact]
    public void Operations_OfAScriptWithoutAnApp_AreEmpty()
        => Assert.Empty(AppShape.Operations(new Dictionary<string, string> { ["tool.cs"] = "public static class Tool { public static void Run() { } }" }));
}
