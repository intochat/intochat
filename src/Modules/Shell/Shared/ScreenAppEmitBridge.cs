using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain;
using DigitalBrain.Errors;
using DigitalBrain.Log;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Flutter;

/// <summary>
/// Bridges built-in <see cref="Screen{T}"/> shorthand UI emits (unprefixed operation names and
/// scalar/positional arguments produced by <c>AppSignals.Emit</c>) to <see cref="IScreen.Request"/>
/// while delegating canonical signal emits directly to <see cref="IScreen.Activate"/>.
/// </summary>
internal static class ScreenAppEmitBridge
{
    private static readonly ILogger ConsoleLog = ShellConsoleLog.For(typeof(ScreenAppEmitBridge));

    public static async Task<ViewActivation> ActivateAsync(
        IScreen screen,
        PublishedView published,
        string screenAlias,
        long targetGen,
        string key,
        string? value,
        string callerPersonId,
        CancellationToken ct = default,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (published.Generation != targetGen)
        {
            throw new ActivationRefusedException(ActivationRefusedException.Stale);
        }

        var node = ViewValidation.Find(published.View, key)
            ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownNode);

        var action = ViewValidation.ActionOf(node);
        switch (action.Kind)
        {
            case ViewActivationKind.Intent:
                return action;

            case ViewActivationKind.Resolve:
                throw new ActivationRefusedException(ActivationRefusedException.NoEmit);

            case ViewActivationKind.Emit:
                var emit = action.Emit!;
                if (!string.Equals(emit.Owner, screenAlias, StringComparison.Ordinal))
                {
                    throw new ActivationRefusedException(ActivationRefusedException.ForeignOwner);
                }

                ViewArgs.RequireValueUnreserved(emit.Args, value);

                if (emit.Signal.Contains('.', StringComparison.Ordinal)
                    && emit.Args is null or { ValueKind: JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined })
                {
                    await screen.Activate(screenAlias, targetGen, key, value, callerPersonId).WaitAsync(ct).ConfigureAwait(false);
                    return action;
                }

                var appPrefix = screenAlias.StartsWith("app.", StringComparison.Ordinal) ? screenAlias[4..] : screenAlias;
                var fullSignal = emit.Signal.Contains('.', StringComparison.Ordinal) ? emit.Signal : $"{appPrefix}.{emit.Signal}";
                if (ResolveSignalType(fullSignal, logger ?? ConsoleLog) is not { } signalType)
                {
                    await screen.Activate(screenAlias, targetGen, key, value, callerPersonId).WaitAsync(ct).ConfigureAwait(false);
                    return action;
                }

                var payload = MapPayload(signalType, emit.Args, value);
                await screen.Request(fullSignal, payload).WaitAsync(ct).ConfigureAwait(false);
                return action;

            default:
                throw new ActivationRefusedException(ActivationRefusedException.NoEmit);
        }
    }

    private static Type? ResolveSignalType(string alias, ILogger logger)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                logger.LogSwallowed(ex, "Partial assembly type load during signal discovery.");
                types = ex.Types;
            }

            foreach (var type in types)
            {
                if (type is null) { continue; }
                var attr = type.GetCustomAttribute<SignalAttribute>(inherit: false);
                if (attr is not null && string.Equals(attr.Alias, alias, StringComparison.Ordinal))
                {
                    return type;
                }
            }
        }

        return null;
    }

    private static JsonElement MapPayload(Type signalType, JsonElement? args, string? value)
    {
        var ctor = signalType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        var parameters = ctor?.GetParameters() ?? [];
        var obj = new JsonObject();

        if (args is { ValueKind: JsonValueKind.Object } objArgs)
        {
            foreach (var prop in objArgs.EnumerateObject())
            {
                obj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
            if (value is not null && !obj.ContainsKey("value"))
            {
                var valueParam = parameters.FirstOrDefault(p => string.Equals(p.Name, "value", StringComparison.OrdinalIgnoreCase));
                var keyName = valueParam?.Name is { Length: > 0 } pn ? Camel(pn) : "value";
                obj[keyName] = valueParam is null ? JsonValue.Create(value) : ConvertScalar(value, valueParam.ParameterType);
            }
        }
        else if (args is { ValueKind: JsonValueKind.Array } arrayArgs)
        {
            var items = arrayArgs.EnumerateArray().ToArray();
            for (var i = 0; i < items.Length && i < parameters.Length; i++)
            {
                var paramName = parameters[i].Name;
                if (!string.IsNullOrEmpty(paramName))
                {
                    obj[Camel(paramName)] = JsonNode.Parse(items[i].GetRawText());
                }
            }
            if (value is not null && items.Length < parameters.Length)
            {
                var nextParam = parameters[items.Length];
                if (!string.IsNullOrEmpty(nextParam.Name))
                {
                    obj[Camel(nextParam.Name)] = ConvertScalar(value, nextParam.ParameterType);
                }
            }
        }
        else if (args is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } scalarArgs)
        {
            if (parameters.Length >= 2 && value is not null)
            {
                AssignScalar(obj, parameters[0], scalarArgs);
                if (!string.IsNullOrEmpty(parameters[1].Name))
                {
                    obj[Camel(parameters[1].Name!)] = ConvertScalar(value, parameters[1].ParameterType);
                }
            }
            else if (parameters.Length >= 1)
            {
                if (scalarArgs.ValueKind == JsonValueKind.String
                    && string.IsNullOrEmpty(scalarArgs.GetString())
                    && value is not null)
                {
                    if (!string.IsNullOrEmpty(parameters[0].Name))
                    {
                        obj[Camel(parameters[0].Name!)] = ConvertScalar(value, parameters[0].ParameterType);
                    }
                }
                else
                {
                    AssignScalar(obj, parameters[0], scalarArgs);
                }
            }
        }
        else if (value is not null && parameters.Length >= 1 && !string.IsNullOrEmpty(parameters[0].Name))
        {
            obj[Camel(parameters[0].Name!)] = ConvertScalar(value, parameters[0].ParameterType);
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    private static void AssignScalar(JsonObject obj, ParameterInfo param, JsonElement scalarArgs)
    {
        if (!string.IsNullOrEmpty(param.Name))
        {
            obj[Camel(param.Name)] = JsonNode.Parse(scalarArgs.GetRawText());
        }
    }

    private static JsonNode? ConvertScalar(string value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying == typeof(bool) && bool.TryParse(value, out var b)) { return JsonValue.Create(b); }
        if (underlying == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { return JsonValue.Create(i); }
        if (underlying == typeof(long) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { return JsonValue.Create(l); }
        if (underlying == typeof(double) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { return JsonValue.Create(d); }
        return JsonValue.Create(value);
    }

    private static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}
