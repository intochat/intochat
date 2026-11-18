using System.Text.Json;

namespace DigitalBrain.UI;

/// <summary>Names the record a control's event delivers as <c>args.event</c>.</summary>
/// <param name="argsType">A record type; anything else is rejected.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class EventArgsAttribute(Type argsType) : Attribute
{
    /// <summary>The record type of the event payload.</summary>
    public Type ArgsType { get; } = IsRecord(argsType)
        ? argsType
        : throw new ArgumentException($"Event args type '{argsType.FullName}' must be a record.", nameof(argsType));

    // Records synthesize a <Clone>$ method; plain classes and strings do not.
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is not null;
}

/// <summary>A button click carries no data.</summary>
public sealed record ClickArgs;

/// <summary>A submitted text.</summary>
/// <param name="Text">The text.</param>
public sealed record SubmitArgs(string Text);

/// <summary>A toggle's new state.</summary>
/// <param name="IsOn">True when on.</param>
public sealed record ToggledArgs(bool IsOn);

/// <summary>The id of the newly selected item.</summary>
/// <param name="Id">The item id.</param>
public sealed record SelectionChangedArgs(string Id);

/// <summary>Something was closed or dismissed.</summary>
public sealed record ClosedArgs;

/// <summary>An expander's new state.</summary>
/// <param name="IsExpanded">True when expanded.</param>
public sealed record ExpandedArgs(bool IsExpanded);

/// <summary>Gives a control property one example value (JSON) for the showcase; repeat for several.</summary>
/// <param name="exampleJson">The example as JSON.</param>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = true)]
public sealed class ShowcaseAttribute(string exampleJson) : Attribute
{
    /// <summary>The example as JSON.</summary>
    public string ExampleJson { get; } = exampleJson;
}

/// <summary>Marks a control property as client-local state that is not part of the persisted view.</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class EphemeralAttribute : Attribute;

/// <summary>Reads the typed payload a control event carries under <c>args.event</c>.</summary>
public static class EventPayload
{
    /// <summary>The property of the signal args that holds the event payload.</summary>
    public const string Key = "event";

    /// <summary>Reads <c>args.event</c> as <typeparamref name="T"/> (camelCase JSON); <paramref name="signal"/> names the signal in the error when it is missing.</summary>
    public static T Read<T>(JsonElement? args, string? signal = null)
    {
        if (args is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(Key, out var payload))
        {
            return payload.Deserialize<T>(WebOptions)
                ?? throw new InvalidOperationException($"Signal '{signal ?? typeof(T).Name}' carried a null '{Key}' payload.");
        }

        throw new InvalidOperationException($"Signal '{signal ?? typeof(T).Name}' args carry no '{Key}' payload.");
    }

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
}
