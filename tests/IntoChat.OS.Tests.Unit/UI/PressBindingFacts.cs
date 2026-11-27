using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.UI;
using IntoChat.OS.UI;

namespace IntoChat.OS.Tests.Unit.UI;

// One press binding: a button or field sends a typed signal as a Command and the screen's [On] reaction handles it. No view
// names a C# method, so every command a compiled screen draws for its own neuron (".") must be a signal that screen reacts to.
public sealed class PressBindingFacts
{
    public static TheoryData<string> Screens()
    {
        var data = new TheoryData<string>();
        foreach (var type in CompiledScreens()) { data.Add(type.FullName!); }
        return data;
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_command_a_screen_draws_for_itself_is_a_signal_it_reacts_to(string screenTypeName)
    {
        var screenType = CompiledScreens().Single(type => type.FullName == screenTypeName);
        var stateType = StateTypeOf(screenType);
        var handled = ScreenSignals.For(screenType);
        var commands = (IReadOnlyList<(string Key, Command Command)>)typeof(PressBindingFacts)
            .GetMethod(nameof(CommandsDrawn), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(stateType)
            .Invoke(null, [screenType])!;

        var unhandled = commands
            .Where(drawn => drawn.Command.Target == "." && !handled.ContainsKey(drawn.Command.Signal))
            .Select(drawn => $"{drawn.Key} -> {drawn.Command.Signal}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(unhandled.Length == 0, $"{screenType.Name} draws commands no [On] reaction handles: {string.Join(", ", unhandled)}");
    }

    // The check bites: a screen that draws a button for a signal it has no reaction to is reported.
    [Fact]
    public void A_drawn_signal_without_a_reaction_is_found()
    {
        var commands = CommandsDrawn<Hello>(typeof(UnhandledPressScreen));

        Assert.Contains(commands, drawn => drawn.Key == "unhandled" && drawn.Command.Signal == "hello.toggle");
        Assert.False(ScreenSignals.For(typeof(UnhandledPressScreen)).ContainsKey("hello.toggle"));
    }

    private sealed class UnhandledPressScreen : AggregateScreen<Hello>
    {
        protected override Node Render(Hello state) => Kit.Button("Unhandled").Key("unhandled").Sends(new HelloToggle());
    }

    // The deleted binding's signal: no screen reacts to it and no view a compiled screen draws carries it.
    [Fact]
    public void No_screen_reacts_to_or_draws_the_method_name_command()
    {
        Assert.Null(typeof(Kit).Assembly.GetType("IntoChat.OS.UI.UiCommand"));
        Assert.Null(typeof(Kit).Assembly.GetType("IntoChat.OS.UI.CommandBinding"));
        foreach (var screenType in CompiledScreens())
        {
            Assert.False(ScreenSignals.For(screenType).ContainsKey("ui.command"), screenType.Name);
        }
    }

    [Fact]
    public void A_button_with_no_action_sends_the_inert_signal_every_screen_ignores()
    {
        var button = Kit.Button("Nothing").ToButton();

        Assert.Equal(".", button.Command!.Target);
        Assert.Equal(UiInert.SignalAlias, button.Command.Signal);
        Assert.Contains(UiInert.SignalAlias, AggregateScreenBase<Hello>.BuiltInSignalAliases);
    }

    // The commands drawn in every state the explorer reaches from the default state (each state rendered as a detached preview).
    private static IReadOnlyList<(string Key, Command Command)> CommandsDrawn<TState>(Type screenType) where TState : class, new()
    {
        var screen = (AggregateScreenBase<TState>)Activator.CreateInstance(screenType)!;
        var graph = AggregateExplorer<TState>.Explore(maxStates: 24);
        var found = new List<(string, Command)>();
        var rendered = 0;
        foreach (var state in graph.States.Select(node => node.State).Prepend(new TState()))
        {
            View view;
            try { view = screen.PreviewView(state); }
#pragma warning disable CA1031 // A state the screen cannot preview outside a live turn draws nothing to check.
            catch (Exception) { continue; }
#pragma warning restore CA1031
            rendered++;
            Collect(RuntimeProtocol.SerializeToElement(view, typeof(View)), "", found);
        }

        Assert.True(rendered > 0, $"{screenType.Name} rendered no state to check.");
        return found;
    }

    private static void Collect(JsonElement element, string key, List<(string, Command)> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { Collect(item, key, found); }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object) { return; }
        var here = element.TryGetProperty("key", out var keyProperty) && keyProperty.ValueKind == JsonValueKind.String ? keyProperty.GetString()! : key;
        if (element.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String
            && element.TryGetProperty("signal", out var signal) && signal.ValueKind == JsonValueKind.String)
        {
            found.Add((here, new Command(target.GetString()!, signal.GetString()!)));
            return;
        }

        foreach (var property in element.EnumerateObject()) { Collect(property.Value, here, found); }
    }

    private static Type StateTypeOf(Type screenType)
    {
        for (var type = screenType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AggregateScreenBase<>)) { return type.GetGenericArguments()[0]; }
        }

        throw new InvalidOperationException($"{screenType.Name} is not an AggregateScreen.");
    }

    // Every concrete compiled screen of the shipped apps and components that can be built without a host.
    private static IEnumerable<Type> CompiledScreens() =>
        new[] { typeof(IntoChat.OS.Apps.Tasks.TasksApp).Assembly, typeof(IntoChat.Aspire.Screens.AspireApp).Assembly, typeof(IntoChat.Muncly.Screens.MunclyApp).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .Where(IsAggregateScreen)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    private static bool IsAggregateScreen(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AggregateScreenBase<>)) { return true; }
        }

        return false;
    }
}
