using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Sandbox;

/// <summary>Metadata describing an exposed <see cref="OnCommandAttribute"/> handler on a deployed app.</summary>
public sealed record SandboxCommandDescriptor(string Name, string Description);

/// <summary>Request payload to compile C# source files into a sandbox-ready PE assembly inside the Sandbox/DevKit worker.</summary>
public sealed record CompileAppRequest(
    string Key,
    Dictionary<string, string> SourceFiles,
    string? AssemblyName = null);

/// <summary>Result of compiling C# source files in the Sandbox/DevKit worker.</summary>
public sealed record CompileAppResponse(
    bool Succeeded,
    string? AssemblyBase64,
    string[] Errors);

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
public sealed partial class AppSandboxHost
{
    private static readonly Uri FallbackKernelUri = new("http://localhost:8080", UriKind.Absolute);

    private readonly ConcurrentDictionary<string, LoadedApp> _apps = new(StringComparer.Ordinal);
    private readonly HttpClient _httpClient;
    private readonly Uri _defaultKernelUri;
    private readonly ILogger<AppSandboxHost> _logger;
    private readonly string? _contractsDirectory;

    [ActivatorUtilitiesConstructor]
    public AppSandboxHost(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AppSandboxHost> logger)
        : this(
            httpClient,
            ResolveConfiguredKernelUri(configuration),
            logger,
            contractsDirectory: null)
    {
    }

    public AppSandboxHost(
        HttpClient httpClient,
        Uri? defaultKernelUri = null,
        ILogger<AppSandboxHost>? logger = null,
        string? contractsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _defaultKernelUri = defaultKernelUri ?? FallbackKernelUri;
        _logger = logger ?? SandboxConsoleLog.For<AppSandboxHost>();
        _contractsDirectory = string.IsNullOrWhiteSpace(contractsDirectory) ? null : contractsDirectory;
        AppLoadContext.ConfigureContractsDirectory(_contractsDirectory);
    }

    public int LoadedAppCount => _apps.Count;

    public CompileAppResponse Compile(CompileAppRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourceFiles is null || request.SourceFiles.Count == 0)
        {
            return new CompileAppResponse(false, null, ["Package contains no C# source files."]);
        }

        var files = new Dictionary<string, string>(request.SourceFiles, StringComparer.Ordinal);
        var isSynapseUnit = files.Values.Any(static code =>
            code.Contains("[Synapse", StringComparison.Ordinal)
            || code.Contains("[DigitalBrain.Synapse", StringComparison.Ordinal));
        if (!isSynapseUnit && !files.ContainsKey("AppGlobalUsings.g.cs"))
        {
            files["AppGlobalUsings.g.cs"] = "global using System.Linq;\nglobal using IntoChat.OS.UI;\nglobal using IntoChat.Sdk;\n";
        }

        var compiler = new DigitalBrain.Runtime.Synapses.SynapseCompiler(DigitalBrain.Runtime.Synapses.ReferenceSet.FromDefaultContext());
        var build = compiler.Compile(new DigitalBrain.Units.SynapseSource(request.Key, files, null));
        if (!build.Success)
        {
            var errors = build.Diagnostics
                .Select(d => $"{d.File}({d.Line}): {d.Id}: {d.Message}")
                .ToArray();
            return new CompileAppResponse(false, null, errors.Length == 0 ? ["Compilation failed."] : errors);
        }

        return new CompileAppResponse(true, Convert.ToBase64String(build.Assembly), []);
    }

    public async Task<DeployAppResponse> DeployAsync(DeployAppRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BrainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AppKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AssemblyBase64);
        cancellationToken.ThrowIfCancellationRequested();

        var key = DeploymentKey(request.BrainId, request.AppKey);
        var assemblyBytes = Convert.FromBase64String(request.AssemblyBase64);
        var loadContext = new AppLoadContext($"{key}@{request.Version}", _contractsDirectory);

        LoadedApp loaded;
        try
        {
            using var stream = new MemoryStream(assemblyBytes, writable: false);
            var assembly = loadContext.LoadFromStream(stream);
            loaded = CreateLoadedApp(request, assembly, loadContext);
        }
        catch
        {
            RuntimeProtocol.Forget(loadContext);
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
                [.. loaded.SignalHandlers.Keys.Concat(loaded.SynapseSignals.Keys).Distinct(StringComparer.OrdinalIgnoreCase)],
                [.. loaded.ScheduleHandlers.Keys.Concat(loaded.SynapseSchedules.Keys).Distinct(StringComparer.OrdinalIgnoreCase)],
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
                        if (loaded.IsSynapseUnit)
                        {
                            if (string.IsNullOrWhiteSpace(request.Name)
                                || !loaded.SynapseSignals.TryGetValue(request.Name, out var synapseSignalHandlers))
                            {
                                handled = false;
                                break;
                            }

                            handled = true;
                            foreach (var descriptor in synapseSignalHandlers)
                            {
                                await InvokeSynapseAsync(loaded, descriptor, request.Name, request.PayloadJson, cancellationToken)
                                    .ConfigureAwait(false);
                            }
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
                            invocationResult = ApplyStateOutcome(
                                loaded,
                                await InvokeHandlerAsync(loaded.Instance!, method, request.PayloadJson, cancellationToken).ConfigureAwait(false));
                        }
                        break;

                    case "schedule":
                        if (loaded.IsSynapseUnit)
                        {
                            if (string.IsNullOrWhiteSpace(request.Name)
                                || !loaded.SynapseSchedules.TryGetValue(request.Name, out var synapseScheduleHandlers))
                            {
                                handled = false;
                                break;
                            }

                            handled = true;
                            foreach (var descriptor in synapseScheduleHandlers)
                            {
                                await InvokeSynapseAsync(loaded, descriptor, request.Name, request.PayloadJson, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            break;
                        }

                        if (string.IsNullOrWhiteSpace(request.Name)
                            || !loaded.ScheduleHandlers.TryGetValue(request.Name, out var scheduleMethods))
                        {
                            handled = false;
                            break;
                        }

                        handled = true;
                        foreach (var method in scheduleMethods)
                        {
                            invocationResult = await InvokeHandlerAsync(loaded.Instance!, method, request.PayloadJson, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        break;

                    case "command":
                        if (string.IsNullOrWhiteSpace(request.Name)
                            || !loaded.Commands.TryGetValue(request.Name, out var command)
                            || loaded.Instance is null)
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
        if (!TryDiscoverAppType(assembly, request.AppKey, out var discovered))
        {
            return CreateLoadedSynapseUnit(request, assembly, loadContext);
        }

        var (appType, appAttribute, brainAppBaseType) = discovered;
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
            commands,
            isSynapseUnit: false,
            synapseState: null,
            slotBindings: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            synapseSignals: new Dictionary<string, List<SynapseDescriptor>>(StringComparer.OrdinalIgnoreCase),
            synapseSchedules: new Dictionary<string, List<SynapseDescriptor>>(StringComparer.OrdinalIgnoreCase));
    }

    private LoadedApp CreateLoadedSynapseUnit(DeployAppRequest request, Assembly assembly, AppLoadContext loadContext)
    {
        var catalog = SynapseCatalog.Scan(assembly);
        if (catalog.Synapses.Count == 0)
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.GetName().Name}' contains no [App] type inheriting BrainApp<TState, TSettings> and no [Synapse] methods.");
        }

        var stateType = catalog.States.Values.FirstOrDefault()
            ?? catalog.Synapses[0].State;
        var initialState = DeserializeOrCreate(request.StateJson, stateType);
        var kernelUri = ResolveKernelUri(request.KernelUrl, _defaultKernelUri);
        var appToken = request.AppToken ?? string.Empty;
        var slotBindings = ParseSlotBindings(request.SettingsJson);

        MethodInfo? renderMethod = null;
        var synapseSignals = new Dictionary<string, List<SynapseDescriptor>>(StringComparer.OrdinalIgnoreCase);
        var synapseSchedules = new Dictionary<string, List<SynapseDescriptor>>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in catalog.Synapses)
        {
            if (descriptor.Kind == SynapseKind.View)
            {
                renderMethod ??= descriptor.Function;
                continue;
            }

            if (descriptor.Every is { Text.Length: > 0 } every)
            {
                if (!synapseSchedules.TryGetValue(every.Text, out var schedList))
                {
                    schedList = [];
                    synapseSchedules[every.Text] = schedList;
                }
                schedList.Add(descriptor);
            }

            var signalAlias = descriptor.SignalAlias
                ?? descriptor.Signal?.GetCustomAttribute<SignalAttribute>(inherit: false)?.Alias
                ?? descriptor.Signal?.Name;
            if (!string.IsNullOrWhiteSpace(signalAlias))
            {
                if (!synapseSignals.TryGetValue(signalAlias, out var sigList))
                {
                    sigList = [];
                    synapseSignals[signalAlias] = sigList;
                }
                sigList.Add(descriptor);
            }
        }

        return new LoadedApp(
            loadContext,
            instance: null,
            stateType,
            settingsType: typeof(Dictionary<string, string>),
            bindMethod: null,
            renderMethod,
            stateProperty: null,
            settingsProperty: null,
            request.BrainId,
            request.AppKey,
            title: request.AppKey,
            summary: string.Empty,
            kernelUri,
            appToken,
            signalHandlers: new Dictionary<string, List<MethodInfo>>(StringComparer.OrdinalIgnoreCase),
            scheduleHandlers: new Dictionary<string, List<MethodInfo>>(StringComparer.OrdinalIgnoreCase),
            commands: new Dictionary<string, CommandEntry>(StringComparer.OrdinalIgnoreCase),
            isSynapseUnit: true,
            synapseState: initialState,
            slotBindings,
            synapseSignals,
            synapseSchedules);
    }

    private bool TryDiscoverAppType(
        Assembly assembly,
        string requestedAppKey,
        out (Type AppType, AppAttribute Attribute, Type BrainAppBaseType) discovered)
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
            discovered = default;
            return false;
        }

        var exact = candidates.FirstOrDefault(c => string.Equals(c.Attribute.Key, requestedAppKey, StringComparison.OrdinalIgnoreCase));
        if (exact.AppType is not null)
        {
            discovered = exact;
            return true;
        }

        if (candidates.Count == 1)
        {
            discovered = candidates[0];
            return true;
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

        if (loaded.IsSynapseUnit)
        {
            if (!string.IsNullOrWhiteSpace(request.StateJson))
            {
                loaded.SynapseState = DeserializeOrCreate(request.StateJson, loaded.StateType);
            }

            if (!string.IsNullOrWhiteSpace(request.SettingsJson))
            {
                loaded.SlotBindings = ParseSlotBindings(request.SettingsJson);
            }

            return;
        }

        var state = !string.IsNullOrWhiteSpace(request.StateJson)
            ? DeserializeOrCreate(request.StateJson, loaded.StateType)
            : loaded.StateProperty!.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.StateType)!;

        var settings = !string.IsNullOrWhiteSpace(request.SettingsJson)
            ? DeserializeOrCreate(request.SettingsJson, loaded.SettingsType)
            : loaded.SettingsProperty!.GetValue(loaded.Instance) ?? Activator.CreateInstance(loaded.SettingsType)!;

        if (needsRebind || !string.IsNullOrWhiteSpace(request.StateJson) || !string.IsNullOrWhiteSpace(request.SettingsJson))
        {
            var client = new IntoChatClient(loaded.BrainId, loaded.AppKey, loaded.KernelUri, loaded.AppToken, _httpClient, _logger);
            loaded.BindMethod!.Invoke(loaded.Instance, [client, state, settings]);
        }
    }

}
