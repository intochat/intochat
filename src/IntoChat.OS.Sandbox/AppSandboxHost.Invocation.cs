using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.Sdk;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Sandbox;

public sealed partial class AppSandboxHost
{
    private async Task InvokeSynapseAsync(
        LoadedApp loaded,
        SynapseDescriptor descriptor,
        string signalAlias,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
        var currentState = loaded.SynapseState ?? DeserializeOrCreate(null, loaded.StateType);
        var parameters = descriptor.Function.GetParameters();
        var args = new object?[parameters.Length];

        using var payloadDoc = TryParseJson(payloadJson);
        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.ParameterType;
            if (param.GetCustomAttribute<DigitalBrain.SlotAttribute>(inherit: false) is { } slotAttr)
            {
                if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Asks<>))
                {
                    throw new NotSupportedException(
                        $"Synapse '{descriptor.Name}' uses legacy Asks<T>; declare the typed capability contract directly with [Slot].");
                }

                var logicalName = slotAttr.Name ?? param.Name ?? string.Empty;
                var targetSlot = loaded.SlotBindings.TryGetValue(logicalName, out var mapped) ? mapped : logicalName;
                args[i] = client.Slot(paramType, string.IsNullOrWhiteSpace(targetSlot) ? null : targetSlot);
                continue;
            }

            if (paramType == loaded.StateType)
            {
                args[i] = currentState;
                continue;
            }

            if (paramType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
                continue;
            }

            if (paramType == typeof(SignalEnvelope))
            {
                var element = payloadDoc?.RootElement.Clone() ?? JsonDocument.Parse("{}").RootElement.Clone();
                args[i] = new SignalEnvelope(signalAlias, element, null, null);
                continue;
            }

            if (typeof(Signal).IsAssignableFrom(paramType))
            {
                var raw = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;
                args[i] = JsonSerializer.Deserialize(raw, paramType, RuntimeProtocol.OptionsFor(paramType))
                    ?? DeserializeOrCreate(null, paramType);
                continue;
            }

            args[i] = FallbackValue(param);
        }

        var rawResult = descriptor.Function.Invoke(null, args);
        object? outcome = rawResult;
        if (rawResult is Task task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            outcome = descriptor.Function.ReturnType.IsGenericType
                ? task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task)
                : null;
        }
        else if (rawResult is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            outcome = null;
        }
        else if (rawResult is not null && rawResult.GetType() is { IsGenericType: true } vtType && vtType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)vtType.GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)!.Invoke(rawResult, null)!;
            await asTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            outcome = asTask.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(asTask);
        }

        if (outcome is null)
        {
            return;
        }

        if (loaded.StateType.IsInstanceOfType(outcome))
        {
            loaded.SynapseState = outcome;
            return;
        }

        var outcomeType = outcome.GetType();
        if (outcomeType.IsGenericType && outcomeType.GetGenericTypeDefinition() == typeof(Reaction<>))
        {
            if (outcomeType.GetProperty("Next", BindingFlags.Instance | BindingFlags.Public)?.GetValue(outcome) is { } nextState)
            {
                loaded.SynapseState = nextState;
            }

            if (outcomeType.GetProperty("Signals", BindingFlags.Instance | BindingFlags.Public)?.GetValue(outcome) is IReadOnlyList<Signal> signals)
            {
                var publishGeneric = typeof(IntoChatClient).GetMethod(nameof(IntoChatClient.PublishAsync), BindingFlags.Instance | BindingFlags.Public)!;
                foreach (var sig in signals)
                {
                    if (sig is null)
                    {
                        continue;
                    }

                    var taskObj = publishGeneric.MakeGenericMethod(sig.GetType()).Invoke(client, [sig, cancellationToken]);
                    if (taskObj is Task publishTask)
                    {
                        await publishTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static async Task<object?> InvokeHandlerAsync(
        object instance,
        MethodInfo method,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        var args = BindParameters(method, payloadJson, cancellationToken);
        var raw = method.Invoke(instance, args);

        if (raw is Task task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (method.ReturnType.IsGenericType)
            {
                return task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(task);
            }

            return null;
        }

        if (raw is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return null;
        }

        if (raw is not null && raw.GetType() is { IsGenericType: true } vtType && vtType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)vtType.GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)!.Invoke(raw, null)!;
            await asTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return asTask.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public)?.GetValue(asTask);
        }

        return raw;
    }

    private static object?[] BindParameters(MethodInfo method, string? payloadJson, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return [];
        }

        var args = new object?[parameters.Length];
        var nonCtCount = parameters.Count(p => p.ParameterType != typeof(CancellationToken));

        using var doc = TryParseJson(payloadJson);
        var hasJson = doc is not null;
        var root = doc?.RootElement ?? default;

        var nonCtIndex = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
                continue;
            }

            args[i] = BindSingleParameter(param, nonCtIndex, nonCtCount, payloadJson, hasJson, root);
            nonCtIndex++;
        }

        return args;
    }

    private static object? BindSingleParameter(
        ParameterInfo param,
        int nonCtIndex,
        int nonCtCount,
        string? rawPayload,
        bool hasJson,
        JsonElement root)
    {
        var targetType = param.ParameterType;

        if (!hasJson)
        {
            if (targetType == typeof(string) && nonCtCount == 1 && rawPayload is not null)
            {
                return rawPayload;
            }

            return FallbackValue(param);
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (param.Name is not null && TryGetPropertyCaseInsensitive(root, param.Name, out var prop))
            {
                return DeserializeElement(prop, targetType);
            }

            if (nonCtCount == 1)
            {
                return DeserializeElement(root, targetType);
            }

            return FallbackValue(param);
        }

        if (root.ValueKind == JsonValueKind.Array && nonCtCount > 1)
        {
            if (nonCtIndex < root.GetArrayLength())
            {
                return DeserializeElement(root[nonCtIndex], targetType);
            }

            return FallbackValue(param);
        }

        if (nonCtCount == 1)
        {
            return DeserializeElement(root, targetType);
        }

        return FallbackValue(param);
    }

    private static object? DeserializeElement(JsonElement element, Type targetType)
    {
        if (targetType == typeof(string) && element.ValueKind != JsonValueKind.String && element.ValueKind != JsonValueKind.Null)
        {
            return element.GetRawText();
        }

        return element.Deserialize(targetType, IntoChatClient.JsonOptions);
    }

    private static object? FallbackValue(ParameterInfo param)
    {
        if (param.HasDefaultValue)
        {
            return param.DefaultValue;
        }

        return param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null;
    }

    private static JsonDocument? TryParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // A signal handler that returns the app's next state, or a Decision on it, has that state bound; a refused Decision is the result.
    private object? ApplyStateOutcome(LoadedApp loaded, object? outcome)
    {
        if (outcome is null || loaded.IsSynapseUnit || loaded.Instance is null || loaded.SettingsProperty is null || loaded.BindMethod is null)
        {
            return outcome;
        }

        var outcomeType = outcome.GetType();
        object? next;
        if (loaded.StateType.IsInstanceOfType(outcome))
        {
            next = outcome;
        }
        else if (outcomeType.IsGenericType && outcomeType.GetGenericTypeDefinition() == typeof(IntoChat.OS.UI.Decision<>)
            && outcomeType.GetGenericArguments()[0] == loaded.StateType)
        {
            var isAccepted = (bool)(outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.IsAccepted))?.GetValue(outcome) ?? false);
            if (!isAccepted)
            {
                return outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.Rejection))?.GetValue(outcome);
            }

            next = outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.Next))?.GetValue(outcome);
        }
        else
        {
            return outcome;
        }

        if (next is not null)
        {
            var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
            var settings = loaded.SettingsProperty.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.SettingsType)!;
            loaded.BindMethod.Invoke(loaded.Instance, [client, next, settings]);
        }

        return null;
    }

    private static string? RenderViewJson(LoadedApp loaded)
    {
        if (loaded.RenderMethod is null)
        {
            return null;
        }

        var view = loaded.IsSynapseUnit
            ? (View?)loaded.RenderMethod.Invoke(null, [loaded.SynapseState ?? DeserializeOrCreate(null, loaded.StateType)])
            : (View?)loaded.RenderMethod.Invoke(loaded.Instance, null);
        if (view is null)
        {
            return null;
        }

        var normalized = view with { Root = IntoChat.OS.UI.Kit.BindOwner(view.Root, loaded.AppKey) };
        return JsonSerializer.Serialize(normalized, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
    }

    private static string SerializeState(LoadedApp loaded)
    {
        var state = loaded.IsSynapseUnit
            ? loaded.SynapseState
            : loaded.StateProperty?.GetValue(loaded.Instance);
        return JsonSerializer.Serialize(state, loaded.StateType, IntoChatClient.JsonOptions);
    }

    private static object DeserializeOrCreate(string? json, Type targetType)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            var deserialized = JsonSerializer.Deserialize(json, targetType, IntoChatClient.JsonOptions);
            if (deserialized is not null)
            {
                return deserialized;
            }
        }

        if (targetType.GetConstructor(Type.EmptyTypes) is not null)
        {
            return Activator.CreateInstance(targetType)!;
        }

        var fromEmptyJson = JsonSerializer.Deserialize("{}", targetType, IntoChatClient.JsonOptions);
        if (fromEmptyJson is not null)
        {
            return fromEmptyJson;
        }

#pragma warning disable SYSLIB0050
        return FormatterServices.GetUninitializedObject(targetType);
#pragma warning restore SYSLIB0050
    }

    private static Dictionary<string, string> ParseSlotBindings(string? settingsJson)
    {
        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var doc = TryParseJson(settingsJson);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return bindings;
        }

        if (doc.RootElement.TryGetProperty("slots", out var slotsElement) && slotsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in slotsElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                {
                    bindings[prop.Name] = prop.Value.GetString()!;
                }
            }
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "slots", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
            {
                bindings.TryAdd(prop.Name, prop.Value.GetString()!);
            }
        }

        return bindings;
    }

    private static async Task UnloadInstanceAsync(LoadedApp loaded, CancellationToken cancellationToken)
    {
        await loaded.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeProtocol.Forget(loaded.LoadContext);
            loaded.LoadContext.Unload();
        }
        finally
        {
            loaded.Gate.Release();
            loaded.Gate.Dispose();
        }
    }

    private static string DeploymentKey(string brainId, string appKey)
        => new DigitalBrain.NeuronId(brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId), "app", appKey).FullKey();

    private static Uri ResolveConfiguredKernelUri(IConfiguration configuration)
        => ResolveKernelUri(configuration["SANDBOX_KERNEL_URL"], FallbackKernelUri);

    private static Uri ResolveKernelUri(string? candidate, Uri fallback)
    {
        var resolved = !string.IsNullOrWhiteSpace(candidate) && Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            ? parsed
            : fallback;

        if (resolved.IsLoopback
            && string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(resolved) { Host = "host.docker.internal" }.Uri;
        }

        return resolved;
    }

    private sealed record CommandEntry(string Name, string Description, MethodInfo Method);

    private sealed class LoadedApp(
        AppLoadContext loadContext,
        object? instance,
        Type stateType,
        Type settingsType,
        MethodInfo? bindMethod,
        MethodInfo? renderMethod,
        PropertyInfo? stateProperty,
        PropertyInfo? settingsProperty,
        string brainId,
        string appKey,
        string title,
        string summary,
        Uri kernelUri,
        string appToken,
        Dictionary<string, List<MethodInfo>> signalHandlers,
        Dictionary<string, List<MethodInfo>> scheduleHandlers,
        Dictionary<string, CommandEntry> commands,
        bool isSynapseUnit,
        object? synapseState,
        Dictionary<string, string> slotBindings,
        Dictionary<string, List<SynapseDescriptor>> synapseSignals,
        Dictionary<string, List<SynapseDescriptor>> synapseSchedules)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AppLoadContext LoadContext { get; } = loadContext;
        public object? Instance { get; } = instance;
        public Type StateType { get; } = stateType;
        public Type SettingsType { get; } = settingsType;
        public MethodInfo? BindMethod { get; } = bindMethod;
        public MethodInfo? RenderMethod { get; } = renderMethod;
        public PropertyInfo? StateProperty { get; } = stateProperty;
        public PropertyInfo? SettingsProperty { get; } = settingsProperty;
        public string BrainId { get; } = brainId;
        public string AppKey { get; } = appKey;
        public string Title { get; } = title;
        public string Summary { get; } = summary;
        public Uri KernelUri { get; set; } = kernelUri;
        public string AppToken { get; set; } = appToken;
        public Dictionary<string, List<MethodInfo>> SignalHandlers { get; } = signalHandlers;
        public Dictionary<string, List<MethodInfo>> ScheduleHandlers { get; } = scheduleHandlers;
        public Dictionary<string, CommandEntry> Commands { get; } = commands;
        public bool IsSynapseUnit { get; } = isSynapseUnit;
        public object? SynapseState { get; set; } = synapseState;
        public Dictionary<string, string> SlotBindings { get; set; } = slotBindings;
        public Dictionary<string, List<SynapseDescriptor>> SynapseSignals { get; } = synapseSignals;
        public Dictionary<string, List<SynapseDescriptor>> SynapseSchedules { get; } = synapseSchedules;
    }
}
