using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Apps.Hosting;
using DigitalBrain.UI;

namespace IntoChat.OS.Modules.Apps.Hosting.InProcess;

public sealed class InProcessAppHost
{
    private readonly Func<Type, string, object> _neuronResolver;
    private readonly Func<Type, object> _capabilityResolver;
    private readonly IPresentationSink _sink;
    private readonly Func<Type, object?>? _services;
    private readonly ConcurrentDictionary<string, HostedProgram> _programs = new(StringComparer.Ordinal);

    public event Action<AppNavigated>? Navigated;

    public InProcessAppHost(
        Func<Type, string, object> neuronResolver,
        Func<Type, object> capabilityResolver,
        IPresentationSink sink,
        object? logger = null,
        Func<Type, object?>? services = null)
    {
        ArgumentNullException.ThrowIfNull(neuronResolver);
        ArgumentNullException.ThrowIfNull(capabilityResolver);
        ArgumentNullException.ThrowIfNull(sink);
        _neuronResolver = neuronResolver;
        _capabilityResolver = capabilityResolver;
        _sink = sink;
        _ = logger;
        _services = services;
    }

    public async Task AssignType(string owner, Type appType, IReadOnlyDictionary<string, string> settings, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(appType);
        ArgumentNullException.ThrowIfNull(settings);
        ct.ThrowIfCancellationRequested();

        if (_programs.TryRemove(owner, out var previous))
        {
            previous.Unload();
        }

        var definition = new AppDefinition(appType);
        HostedProgram? hosted = null;
        var app = CreateApp(definition, () => hosted!);
        var turns = new AppTurns(app, definition, token => PresentCurrentAsync(hosted!, token));
        var hash = appType.Assembly.GetName().Version?.ToString() ?? "compiled";
        var behaviors = new[] { new BehaviorView(owner, hash, "Running", null) };
        hosted = new HostedProgram(owner, hash, turns, settings, context: null, behaviors);
        _programs[owner] = hosted;
        await PresentCurrentAsync(hosted, ct).ConfigureAwait(false);
    }

    public Task Retire(string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ct.ThrowIfCancellationRequested();
        UnloadProgram(owner);
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UnloadProgram(string owner)
    {
        if (_programs.TryRemove(owner, out var removed))
        {
            removed.Unload();
        }
    }

    public Task<BehaviorView[]> Describe(string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_programs.TryGetValue(owner, out var hosted) ? hosted.Behaviors : []);
    }

    public Task Request(string owner, string operation, object? argument = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (!_programs.TryGetValue(owner, out var hosted))
        {
            throw new InvalidOperationException($"No program is hosted for owner '{owner}'.");
        }
        hosted.LastCause = operation;
        return hosted.Turns.Request(operation, argument, ct);
    }

    public AppTurns? TryGetTurns(string owner)
        => _programs.TryGetValue(owner, out var hosted) ? hosted.Turns : null;

    private App CreateApp(AppDefinition definition, Func<IPresenter> presenter)
    {
        var instance = definition.Create(serviceType =>
        {
            if (serviceType == typeof(IPresenter))
            {
                return presenter();
            }
            if (_services?.Invoke(serviceType) is { } resolved)
            {
                return resolved;
            }
            if (serviceType.IsInterface && serviceType.IsDefined(typeof(CapabilityAttribute), inherit: false))
            {
                return _capabilityResolver(serviceType);
            }
            return null;
        });
        instance.AttachNeuronResolver(_neuronResolver);
        instance.AttachCapabilityResolver(_capabilityResolver);
        return instance;
    }

    private Task PresentCurrentAsync(HostedProgram hosted, CancellationToken ct)
    {
        var type = hosted.Turns.App.GetType();
        var (presentedSide, presentedMain) = hosted.ConsumePresented();
        var side = ReadProperty(type, hosted.Turns.App, "Side")
            ?? InvokeRender(type, hosted.Turns.App, "RenderSide", "Side")
            ?? presentedSide;
        var main = ReadProperty(type, hosted.Turns.App, "Main")
            ?? InvokeRender(type, hosted.Turns.App, "Render", hosted.Turns.Definition.Name)
            ?? presentedMain;
        if (main is not null)
        {
            var toView = RootViewOf(main);
            if (!string.Equals(hosted.LastRootView, toView, StringComparison.Ordinal))
            {
                var fromView = hosted.LastRootView;
                hosted.LastRootView = toView;
                Navigated?.Invoke(new AppNavigated(hosted.Owner, fromView, toView, hosted.LastCause ?? "open"));
            }
        }
        return side is not null || main is not null
            ? _sink.Present(hosted.Owner, side, main, ct)
            : Task.CompletedTask;
    }

    private static string RootViewOf(View view)
    {
        var title = string.IsNullOrWhiteSpace(view.Title) ? "main" : view.Title;
        var active = FindActiveTag(view.Root, depth: 0);
        return string.IsNullOrWhiteSpace(active) ? title : $"{title}:{active}";
    }

    private static string? FindActiveTag(Node? node, int depth)
    {
        if (node is null || depth > 4) { return null; }
        switch (node)
        {
            case Tabs { SelectedId: { Length: > 0 } tab }:
                return tab;
            case NavItemNode { Selected: true } nav:
                return !string.IsNullOrWhiteSpace(nav.Key) ? nav.Key : nav.Label;
            case AppShellNode shell:
                return FindActiveTag(shell.Toolbar, depth + 1)
                    ?? FindActiveTag(shell.Sidebar, depth + 1)
                    ?? FindActiveTag(shell.Body, depth + 1);
            case Column col:
                foreach (var child in col.Children)
                {
                    if (FindActiveTag(child, depth + 1) is { } found) { return found; }
                }
                return null;
            case Row row:
                foreach (var child in row.Children)
                {
                    if (FindActiveTag(child, depth + 1) is { } found) { return found; }
                }
                return null;
            case Surface surface:
                foreach (var child in surface.Children)
                {
                    if (FindActiveTag(child, depth + 1) is { } found) { return found; }
                }
                return null;
            case SplitPane split:
                return FindActiveTag(split.Leading, depth + 1) ?? FindActiveTag(split.Trailing, depth + 1);
            default:
                return null;
        }
    }

    private static View? ReadProperty(Type type, App instance, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (prop is null || !(typeof(View).IsAssignableFrom(prop.PropertyType) || typeof(Node).IsAssignableFrom(prop.PropertyType)))
        {
            return null;
        }
        var value = prop.GetValue(instance);
        return value as View ?? (value is Node node ? new View(name, node) : null);
    }

    private static View? InvokeRender(Type type, App instance, string methodName, string fallbackTitle)
    {
        var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
        if (method is null || !(typeof(View).IsAssignableFrom(method.ReturnType) || typeof(Node).IsAssignableFrom(method.ReturnType)))
        {
            return null;
        }
        var value = method.Invoke(instance, null);
        return value as View ?? (value is Node node ? new View(fallbackTitle, node) : null);
    }
}
