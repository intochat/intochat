using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>
/// The signal of a button or field that has no action of its own: a field drawn only for its label and value, or a button
/// built without a command or intent. The screen hears it and does nothing.
/// </summary>
[Signal(SignalAlias)]
public sealed record UiInert : Signal
{
    /// <summary>Frozen signal alias handled by every <see cref="AggregateScreenBase{TState}"/>.</summary>
    public const string SignalAlias = "ui.inert";
}

// Display metadata a Kit control carries in its command's args for the client (a guard's reason, a field's label and value).
internal static class CommandMetadata
{
    public static Command Inert() => new(".", UiInert.SignalAlias);

    public static Command WithHint(Command command, string? guardReason, string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(guardReason) && string.IsNullOrWhiteSpace(tooltip))
        {
            return command;
        }

        var map = ArgsOf(command);
        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            map["guardReason"] = RuntimeProtocol.SerializeToElement(guardReason, typeof(string));
        }

        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            map["tooltip"] = RuntimeProtocol.SerializeToElement(tooltip, typeof(string));
        }

        return command with { Args = RuntimeProtocol.SerializeToElement(map, typeof(Dictionary<string, JsonElement>)) };
    }

    public static Command? WithField(
        Command? command,
        bool hasExplicitLabel,
        string? explicitLabel,
        string? controlledValue,
        string? helper,
        string? error,
        bool liveChange)
    {
        var hasAnyMeta =
            hasExplicitLabel ||
            controlledValue is not null ||
            !string.IsNullOrWhiteSpace(helper) ||
            !string.IsNullOrWhiteSpace(error) ||
            liveChange;
        if (!hasAnyMeta)
        {
            return command;
        }

        // A field with no command still carries its metadata, on an inert command to its own view's neuron.
        var baseCommand = command is null ? Inert()
            : string.IsNullOrEmpty(command.Target) ? command with { Target = "." } : command;
        var map = ArgsOf(baseCommand);
        if (hasExplicitLabel)
        {
            map["uiLabel"] = RuntimeProtocol.SerializeToElement(explicitLabel ?? string.Empty, typeof(string));
        }

        if (controlledValue is not null)
        {
            map["uiValue"] = RuntimeProtocol.SerializeToElement(controlledValue, typeof(string));
        }

        if (!string.IsNullOrWhiteSpace(helper))
        {
            map["uiHelper"] = RuntimeProtocol.SerializeToElement(helper, typeof(string));
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            map["uiError"] = RuntimeProtocol.SerializeToElement(error, typeof(string));
        }

        if (liveChange)
        {
            map["uiLive"] = RuntimeProtocol.SerializeToElement(true, typeof(bool));
        }

        return baseCommand with { Args = RuntimeProtocol.SerializeToElement(map, typeof(Dictionary<string, JsonElement>)) };
    }

    private static Dictionary<string, JsonElement> ArgsOf(Command command)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (command.Args is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var prop in existing.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }

        return map;
    }
}
