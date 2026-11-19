using System.Reflection;
using System.Text.Json;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;

namespace DigitalBrain.Tests.Unit.UI;

public sealed class CommandsFacts
{
    [Fact]
    public void Command_target_dot_means_self()
    {
        var command = new Command(".", "control.toggle");

        Assert.Equal(".", command.Target);
        Assert.Null(command.Args);
    }

    [Fact]
    public void Command_round_trips_json_with_web_casing()
    {
        using var args = JsonDocument.Parse("""{"isOn":true}""");
        var command = new Command("dark-mode", "control.setChecked", args.RootElement.Clone());

        var json = JsonSerializer.Serialize(command, JsonSerializerOptions.Web);
        var back = JsonSerializer.Deserialize<Command>(json, JsonSerializerOptions.Web);

        Assert.Equal("""{"target":"dark-mode","signal":"control.setChecked","args":{"isOn":true}}""", json);
        Assert.NotNull(back);
        Assert.Equal("dark-mode", back.Target);
        Assert.Equal("control.setChecked", back.Signal);
        Assert.True(back.Args!.Value.GetProperty("isOn").GetBoolean());
    }

    [Fact]
    public void Every_control_command_has_a_signal_alias()
    {
        Type[] commands = [typeof(Toggle), typeof(SetChecked), typeof(DigitalBrain.UI.Commands.Select), typeof(Expand), typeof(Submit), typeof(Close)];

        var aliases = commands.Select(type => type.GetCustomAttribute<SignalAttribute>()?.Alias).ToArray();

        Assert.All(aliases, alias => Assert.StartsWith("control.", alias));
        Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        Assert.All(commands, type => Assert.True(typeof(Signal).IsAssignableFrom(type)));
    }
}
