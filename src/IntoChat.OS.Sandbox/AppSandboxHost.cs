using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using DigitalBrain.UI;
using IntoChat.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Sandbox;

/// <summary>Metadata describing an exposed <see cref="OnCommandAttribute"/> handler on a deployed app.</summary>
public sealed record SandboxCommandDescriptor(string Name, string Description);

/// <summary>Request payload to load and bind a compiled app assembly into the sandbox worker.</summary>
public sealed record DeployAppRequest(
    string BrainId,
    string AppKey,
    string Version,
    string Digest,
    string AssemblyBase64,
    string? StateJson = null,
    string? SettingsJson = null,
    string? KernelUrl = null,
    string? AppToken = null);

/// <summary>Deployment summary returned after discovering handlers and rendering the initial view.</summary>
public sealed record DeployAppResponse(
    string BrainId,
    string AppKey,
    string Version,
    string Digest,
    string Title,
    string Summary,
    SandboxCommandDescriptor[] Commands,
    string[] Signals,
    string[] Schedules,
    string? InitialViewJson,
    string? StateJson = null);

/// <summary>Request payload to dispatch a signal, schedule, command, or render pass to a loaded app.</summary>
public sealed record DispatchAppRequest(
    string BrainId,
    string AppKey,
    string Kind,
    string? Name = null,
    string? PayloadJson = null,
    string? StateJson = null,
    string? SettingsJson = null,
    string? KernelUrl = null,
    string? AppToken = null);

/// <summary>Result of dispatching an event or render pass to a loaded app.</summary>
public sealed record DispatchAppResponse(
    bool Handled,
    string? ResultJson,
    string? UpdatedStateJson,
    string? UpdatedViewJson,
    string? Error = null);

/// <summary>Request payload to unload a deployed app from the sandbox worker.</summary>
public sealed record UnloadAppRequest(string BrainId, string AppKey);

/// <summary>Result of unloading an app from the sandbox worker.</summary>
public sealed record UnloadAppResponse(bool Unloaded, string BrainId, string AppKey);

/// <summary>DI singleton hosting isolated <see cref="AppLoadContext"/> instances for deployed brain apps.</summary>
public sealed class AppSandboxHost
{
    private static readonly Uri FallbackKernelUri = new("http://localhost:8080", UriKind.Absolute);

    private readonly ConcurrentDictionary<string, LoadedApp> _apps = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly Uri _defaultKernelUri;
    private readonly ILogger<AppSandboxHost> _logger;

    [ActivatorUtilitiesConstructor]
    public AppSandboxHost(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AppSandboxHost> logger)
        : this(
            httpClient,
            ResolveConfiguredKernelUri(configuration),
            logger)
    {
    }

    public AppSandboxHost(
        HttpClient httpClient,
        Uri? defaultKernelUri = null,
        ILogger<AppSandboxHost>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _defaultKernelUri = defaultKernelUri ?? FallbackKernelUri;
        _logger = logger ?? SandboxConsoleLog.For<AppSandboxHost>();
    }

    public int LoadedAppCount => _apps.Count;

    public async Task<DeployAppResponse> DeployAsync(DeployAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AssemblyBase64);
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeploymentKey(request.BrainId, request.AppKey);
        var assemblyBytes = Convert.FromBase64String(request.AssemblyBase64);
        var loadContext = new AppLoadContext($"{key}@{request.Version}");

        LoadedApp loaded;
        try
        {
            using var stream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = loadContext.LoadFromStream(stream);
            loaded = CreateLoadedApp(request, assembly, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }

        if (_apps.TryRemove(key, out var previous))
        {
            await UnloadInstanceAsync(previous, cancellationToken).ConfigureAwait(false);
        }

        _apps[key] = loaded;

        await loaded.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var initialViewJson = RenderViewJson(loaded);
            var initialStateJson = SerializeState(loaded);

            _logger.LogInformation(
                "Deployed sandbox app {BrainId}/{AppKey} version {Version} ({CommandCount} commands, {SignalCount} signals, {ScheduleCount} schedules).",
                request.BrainId,
                request.AppKey,
                request.Version,
                loaded.Commands.Count,
                loaded.SignalHandlers.Count,
                loaded.ScheduleHandlers.Count);

            return new DeployAppResponse(
                request.BrainId,
                request.AppKey,
                request.Version,
                request.Digest,
                loaded.Title,
                loaded.Summary,
                [.. loaded.Commands.Values.Select(c => new SandboxCommandDescriptor(c.Name, c.Description))],
                [.. loaded.SignalHandlers.Keys],
                [.. loaded.ScheduleHandlers.Keys],
                initialViewJson,
                initialStateJson);
        }
        finally
        {
            loaded.Gate.Release();
        }
    }

    public async Task<DispatchAppResponse> DispatchAsync(DispatchAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);

        var key = DeploymentKey(request.BrainId, request.AppKey);
        if (!_apps.TryGetValue(key, out var loaded))
        {
            throw new KeyNotFoundException($"Sandbox app '{key}' is not deployed.");
        }

        await loaded.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefreshDeploymentBindings(loaded, request);

            object? invocationResult = null;
            bool handled;

            try
            {
                switch (request.Kind.ToLowerInvariant())
                {
                    case "render":
                        handled = true;
                        break;

                    case "signal":
                        if (string.Equals(request.Name, IntoChat.OS.UI.UiCommand.SignalAlias, StringComparison.OrdinalIgnoreCase))
                        {
                            handled = true;
                            invocationResult = DispatchReactorUiCommand(loaded, request.PayloadJson);
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(request.Name)
                            || !loaded.SignalHandlers.TryGetValue(request.Name, out var signalMethods))
                        {
                            handled = false;
                            break;
                        }

                        handled = true;
                        foreach (var method in signalMethods)
                        {
                            invocationResult = await InvokeHandlerAsync(loaded.Instance, method, request.PayloadJson, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        break;

                    case "schedule":
                        if (string.IsNullOrWhiteSpace(request.Name)
                            || !loaded.ScheduleHandlers.TryGetValue(request.Name, out var scheduleMethods))
                        {
                            handled = false;
                            break;
                        }

                        handled = true;
                        foreach (var method in scheduleMethods)
                        {
                            invocationResult = await InvokeHandlerAsync(loaded.Instance, method, request.PayloadJson, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        break;

                    case "command":
                        if (string.Equals(request.Name, IntoChat.OS.UI.UiCommand.SignalAlias, StringComparison.OrdinalIgnoreCase))
                        {
                            handled = true;
                            invocationResult = DispatchReactorUiCommand(loaded, request.PayloadJson);
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(request.Name)
                            || !loaded.Commands.TryGetValue(request.Name, out var command))
                        {
                            handled = false;
                            break;
                        }

                        handled = true;
                        invocationResult = await InvokeHandlerAsync(loaded.Instance, command.Method, request.PayloadJson, cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    default:
                        return new DispatchAppResponse(
                            Handled: false,
                            ResultJson: null,
                            UpdatedStateJson: SerializeState(loaded),
                            UpdatedViewJson: null,
                            Error: $"Unsupported dispatch kind '{request.Kind}'.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var unwrapped = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                _logger.LogError(unwrapped, "Error dispatching {Kind} '{Name}' to {BrainId}/{AppKey}.", request.Kind, request.Name, request.BrainId, request.AppKey);

                return new DispatchAppResponse(
                    Handled: true,
                    ResultJson: null,
                    UpdatedStateJson: SerializeState(loaded),
                    UpdatedViewJson: null,
                    Error: unwrapped.Message);
            }

            var resultJson = invocationResult is null
                ? null
                : JsonSerializer.Serialize(invocationResult, invocationResult.GetType(), IntoChatClient.JsonOptions);
            var updatedStateJson = SerializeState(loaded);
            var updatedViewJson = RenderViewJson(loaded);

            return new DispatchAppResponse(handled, resultJson, updatedStateJson, updatedViewJson);
        }
        finally
        {
            loaded.Gate.Release();
        }
    }

    public async Task<UnloadAppResponse> UnloadAsync(string brainId, string appKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var key = DeploymentKey(brainId, appKey);
        if (!_apps.TryRemove(key, out var loaded))
        {
            return new UnloadAppResponse(Unloaded: false, brainId, appKey);
        }

        await UnloadInstanceAsync(loaded, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Unloaded sandbox app {BrainId}/{AppKey}.", brainId, appKey);
        return new UnloadAppResponse(Unloaded: true, brainId, appKey);
    }

    private LoadedApp CreateLoadedApp(DeployAppRequest request, Assembly assembly, AppLoadContext loadContext)
    {
        var (appType, appAttribute, brainAppBaseType) = DiscoverAppType(assembly, request.AppKey);
        var typeArgs = brainAppBaseType.GetGenericArguments();
        var stateType = typeArgs[0];
        var settingsType = typeArgs[1];

        var instance = Activator.CreateInstance(appType)
            ?? throw new InvalidOperationException($"Could not instantiate app type '{appType.FullName}'.");

        var state = DeserializeOrCreate(request.StateJson, stateType);
        var settings = DeserializeOrCreate(request.SettingsJson, settingsType);
        var kernelUri = ResolveKernelUri(request.KernelUrl, _defaultKernelUri);
        var appToken = request.AppToken ?? string.Empty;

        var bindMethod = brainAppBaseType.GetMethod(nameof(BrainApp<object, object>.Bind), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(brainAppBaseType.FullName, nameof(BrainApp<object, object>.Bind));
        var renderMethod = appType.GetMethod(nameof(BrainApp<object, object>.Render), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(appType.FullName, nameof(BrainApp<object, object>.Render));
        var stateProperty = brainAppBaseType.GetProperty(nameof(BrainApp<object, object>.State), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(brainAppBaseType.FullName, nameof(BrainApp<object, object>.State));
        var settingsProperty = brainAppBaseType.GetProperty(nameof(BrainApp<object, object>.Settings), BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMemberException(brainAppBaseType.FullName, nameof(BrainApp<object, object>.Settings));

        var client = new IntoChatClient(request.BrainId, request.AppKey, kernelUri, appToken, _httpClient, _logger);
        bindMethod.Invoke(instance, [client, state, settings]);

        var signalHandlers = new Dictionary<string, List<MethodInfo>>(StringComparer.OrdinalIgnoreCase);
        var scheduleHandlers = new Dictionary<string, List<MethodInfo>>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase);

        var methods = appType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var method in methods)
        {
            if (method.DeclaringType == typeof(object) || method.DeclaringType == brainAppBaseType)
            {
                continue;
            }

            foreach (var onSignal in method.GetCustomAttributes<OnSignalAttribute>(inherit: false))
            {
                if (!signalHandlers.TryGetValue(onSignal.Signal, out var list))
                {
                    list = [];
                    signalHandlers[onSignal.Signal] = list;
                }
                list.Add(method);
            }

            foreach (var every in method.GetCustomAttributes<EveryAttribute>(inherit: false))
            {
                if (!scheduleHandlers.TryGetValue(every.Cron, out var list))
                {
                    list = [];
                    scheduleHandlers[every.Cron] = list;
                }
                list.Add(method);
            }

            if (method.GetCustomAttribute<OnCommandAttribute>(inherit: false) is { } onCommand)
            {
                commands[onCommand.Name] = new CommandEntry(onCommand.Name, onCommand.Description, method);
            }
        }

        var title = string.IsNullOrWhiteSpace(appAttribute.Title) ? appAttribute.Key : appAttribute.Title;
        return new LoadedApp(
            loadContext,
            instance,
            stateType,
            settingsType,
            bindMethod,
            renderMethod,
            stateProperty,
            settingsProperty,
            request.BrainId,
            request.AppKey,
            title,
            appAttribute.Summary,
            kernelUri,
            appToken,
            signalHandlers,
            scheduleHandlers,
            commands);
    }

    private (Type AppType, AppAttribute Attribute, Type BrainAppBaseType) DiscoverAppType(Assembly assembly, string requestedAppKey)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = [.. ex.Types.Where(static t => t is not null).Cast<Type>()];
        }

        var candidates = new List<(Type AppType, AppAttribute Attribute, Type BrainAppBaseType)>();
        foreach (var type in types)
        {
            try
            {
                if (type is not { IsClass: true, IsAbstract: false })
                {
                    continue;
                }

                var brainAppBase = FindBrainAppBaseType(type);
                if (brainAppBase is null)
                {
                    continue;
                }

                var appAttr = type.GetCustomAttribute<AppAttribute>(inherit: false);
                if (appAttr is null)
                {
                    continue;
                }

                candidates.Add((type, appAttr, brainAppBase));
            }
            catch (Exception ex) when (ex is TypeLoadException or FileLoadException or FileNotFoundException or BadImageFormatException)
            {
                _logger.LogDebug(ex, "Skipping non-app type {TypeName} during sandbox discovery.", type.FullName);
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.GetName().Name}' contains no [App] type inheriting BrainApp<TState, TSettings>.");
        }

        var exact = candidates.FirstOrDefault(c => string.Equals(c.Attribute.Key, requestedAppKey, StringComparison.OrdinalIgnoreCase));
        if (exact.AppType is not null)
        {
            return exact;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        throw new InvalidOperationException(
            $"Assembly '{assembly.GetName().Name}' contains multiple [App] types and none match key '{requestedAppKey}'.");
    }

    private static Type? FindBrainAppBaseType(Type type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(BrainApp<,>))
            {
                return current;
            }

            current = current.BaseType;
        }

        return null;
    }

    private void RefreshDeploymentBindings(LoadedApp loaded, DispatchAppRequest request)
    {
        var needsRebind = false;

        if (!string.IsNullOrWhiteSpace(request.KernelUrl))
        {
            loaded.KernelUri = ResolveKernelUri(request.KernelUrl, loaded.KernelUri);
            needsRebind = true;
        }

        if (request.AppToken is not null)
        {
            loaded.AppToken = request.AppToken;
            needsRebind = true;
        }

        var state = !string.IsNullOrWhiteSpace(request.StateJson)
            ? DeserializeOrCreate(request.StateJson, loaded.StateType)
            : loaded.StateProperty.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.StateType)!;

        var settings = !string.IsNullOrWhiteSpace(request.SettingsJson)
            ? DeserializeOrCreate(request.SettingsJson, loaded.SettingsType)
            : loaded.SettingsProperty.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.SettingsType)!;

        if (needsRebind || !string.IsNullOrWhiteSpace(request.StateJson) || !string.IsNullOrWhiteSpace(request.SettingsJson))
        {
            var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
            loaded.BindMethod.Invoke(loaded.Instance, [client, state, settings]);
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

    private object? DispatchReactorUiCommand(LoadedApp loaded, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        var uiCommand = JsonSerializer.Deserialize<IntoChat.OS.UI.UiCommand>(
            payloadJson,
            DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(IntoChat.OS.UI.UiCommand)));
        if (uiCommand is null || string.IsNullOrWhiteSpace(uiCommand.Method))
        {
            return null;
        }

        var currentState = loaded.StateProperty.GetValue(loaded.Instance)
            ?? Activator.CreateInstance(loaded.StateType)!;
        var method = loaded.StateType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => string.Equals(m.Name, uiCommand.Method, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown aggregate command '{loaded.StateType.Name}.{uiCommand.Method}'.");

        var callArgs = BindReactorArguments(method, uiCommand);
        var outcome = method.Invoke(currentState, callArgs);
        if (outcome is null)
        {
            return null;
        }

        var outcomeType = outcome.GetType();
        if (loaded.StateType.IsInstanceOfType(outcome))
        {
            var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
            var settings = loaded.SettingsProperty.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.SettingsType)!;
            loaded.BindMethod.Invoke(loaded.Instance, [client, outcome, settings]);
            return null;
        }

        if (outcomeType.IsGenericType && outcomeType.GetGenericTypeDefinition() == typeof(IntoChat.OS.UI.Decision<>))
        {
            var isAccepted = (bool)(outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.IsAccepted))?.GetValue(outcome) ?? false);
            if (!isAccepted)
            {
                return outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.Rejection))?.GetValue(outcome);
            }

            var next = outcomeType.GetProperty(nameof(IntoChat.OS.UI.Decision<object>.Next))?.GetValue(outcome);
            if (next is not null)
            {
                var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
                var settings = loaded.SettingsProperty.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.SettingsType)!;
                loaded.BindMethod.Invoke(loaded.Instance, [client, next, settings]);
            }

            return null;
        }

        return outcome;
    }

    private static object?[] BindReactorArguments(MethodInfo method, IntoChat.OS.UI.UiCommand command)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return [];
        }

        var args = new object?[parameters.Length];
        var provided = command.Args ?? [];
        var cursor = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                args[i] = CancellationToken.None;
                continue;
            }

            if (command.ValueIndex == cursor)
            {
                args[i] = command.Value ?? (param.HasDefaultValue ? param.DefaultValue : string.Empty);
                cursor++;
                continue;
            }

            args[i] = cursor < provided.Length
                ? provided[cursor].Deserialize(param.ParameterType, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(param.ParameterType))
                : FallbackValue(param);

            cursor++;
        }

        return args;
    }

    private static string? RenderViewJson(LoadedApp loaded)
    {
        using var scope = IntoChat.OS.UI.CommandBinding.PushOwner(loaded.AppKey);
        var view = (View?)loaded.RenderMethod.Invoke(loaded.Instance, null);
        if (view is null)
        {
            return null;
        }

        var normalized = view with { Root = IntoChat.OS.UI.Kit.BindOwner(view.Root, loaded.AppKey) };
        return JsonSerializer.Serialize(normalized, DigitalBrain.Invocation.RuntimeProtocol.OptionsFor(typeof(View)));
    }

    private static string SerializeState(LoadedApp loaded)
    {
        var state = loaded.StateProperty.GetValue(loaded.Instance);
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

        return Activator.CreateInstance(targetType)
            ?? throw new InvalidOperationException($"Could not instantiate '{targetType.FullName}'.");
    }

    private static async Task UnloadInstanceAsync(LoadedApp loaded, CancellationToken cancellationToken)
    {
        await loaded.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            loaded.LoadContext.Unload();
        }
        finally
        {
            loaded.Gate.Release();
            loaded.Gate.Dispose();
        }
    }

    private static string DeploymentKey(string brainId, string appKey) => $"{brainId}/{appKey}";

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
        object instance,
        Type stateType,
        Type settingsType,
        MethodInfo bindMethod,
        MethodInfo renderMethod,
        PropertyInfo stateProperty,
        PropertyInfo settingsProperty,
        string brainId,
        string appKey,
        string title,
        string summary,
        Uri kernelUri,
        string appToken,
        Dictionary<string, List<MethodInfo>> signalHandlers,
        Dictionary<string, List<MethodInfo>> scheduleHandlers,
        Dictionary<string, CommandEntry> commands)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AppLoadContext LoadContext { get; } = loadContext;
        public object Instance { get; } = instance;
        public Type StateType { get; } = stateType;
        public Type SettingsType { get; } = settingsType;
        public MethodInfo BindMethod { get; } = bindMethod;
        public MethodInfo RenderMethod { get; } = renderMethod;
        public PropertyInfo StateProperty { get; } = stateProperty;
        public PropertyInfo SettingsProperty { get; } = settingsProperty;
        public string BrainId { get; } = brainId;
        public string AppKey { get; } = appKey;
        public string Title { get; } = title;
        public string Summary { get; } = summary;
        public Uri KernelUri { get; set; } = kernelUri;
        public string AppToken { get; set; } = appToken;
        public Dictionary<string, List<MethodInfo>> SignalHandlers { get; } = signalHandlers;
        public Dictionary<string, List<MethodInfo>> ScheduleHandlers { get; } = scheduleHandlers;
        public Dictionary<string, CommandEntry> Commands { get; } = commands;
    }
}
