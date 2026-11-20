using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>
/// Optional display metadata for an <see cref="AggregateScreen{TState}"/> (title and Lucide icon slug).
/// Pair with <see cref="NeuronAttribute"/> to declare the frozen neuron alias.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ScreenAttribute(string? alias = null) : Attribute
{
    /// <summary>Optional alias hint (falls back to <see cref="NeuronAttribute.Type"/>).</summary>
    public string? Alias { get; } = alias;

    /// <summary>Window / screen title shown in <see cref="View.Title"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Optional Lucide icon slug representing the screen.</summary>
    public string? Icon { get; init; }
}

/// <summary>
/// Base class sealing <see cref="Screen{TState}.Render(TState)"/> so <see cref="AggregateScreen{TState}"/> can expose
/// <c>protected override Node Render(TState state)</c> directly while preserving 100% compatibility with
/// <see cref="Screen{TState}"/>, Orleans hosting, <c>TestOS</c>, and MCP <c>screen_read</c>/<c>screen_act</c>.
/// </summary>
public abstract class AggregateScreenBase<TState> : Screen<TState> where TState : class, new()
{
    private static readonly ConditionalWeakTable<Type, CommandDispatchTable> DispatchTables = new();
    private static readonly ConditionalWeakTable<Type, NavigationDispatchTable> NavigationTables = new();

    /// <summary>Frozen signal aliases handled natively by every <see cref="AggregateScreenBase{TState}"/>.</summary>
    public static IReadOnlyList<string> BuiltInSignalAliases { get; } =
    [
        UiCommand.SignalAlias,
        UiNavigateTo.SignalAlias,
        UiNavigateFrom.SignalAlias,
    ];

    /// <summary>
    /// Transient record of the most recent <see cref="Rejected"/> outcome produced when a command or departure was refused.
    /// Cleared automatically when a subsequent command or departure is accepted.
    /// </summary>
    public Rejected? LastRejection { get; private set; }

    /// <summary>
    /// Active <see cref="ViewTransition"/> token established by the most recent <see cref="NavigateToAsync{TArgs}(NavigationArrival{TArgs}, CancellationToken)"/>
    /// or <see cref="UiNavigateTo"/> signal (empty prior to navigation).
    /// </summary>
    public string ActiveTransition { get; protected set; } = string.Empty;

    /// <summary>Title written to <see cref="View.Title"/>; defaults to <see cref="ScreenAttribute.Title"/> or the humanized class name.</summary>
    protected virtual string Title => ResolveDefaultTitle(GetType());

    /// <inheritdoc />
    protected sealed override View Render(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var owner = SafeOwnerAlias();
        using var scope = CommandBinding.PushOwner(owner);
        var root = RenderNodeCore(state);
        var normalized = NodeOwnerNormalizer.Normalize(root, owner);
        if (normalized is AppShellNode { Transition.Length: 0 } shell && !string.IsNullOrEmpty(ActiveTransition))
        {
            normalized = shell with { Transition = ActiveTransition };
        }

        return new View(Title, normalized);
    }

    /// <summary>Builds a detached <see cref="Node"/> preview for <paramref name="state"/> (used by <see cref="AggregateExplorer{TState}"/> and unit tests).</summary>
    public Node PreviewNode(TState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return PreviewView(state).Root;
    }

    /// <summary>Builds a detached <see cref="View"/> preview for <paramref name="state"/> without requiring grain activation; slots render without changing the live view's routing.</summary>
    public View PreviewView(TState state) => RenderDetached(state);

    private protected abstract Node RenderNodeCore(TState state);

    /// <summary>
    /// Built-in <c>[On]</c> command dispatcher invoked automatically when an interactive <see cref="Kit"/> control
    /// bound via <c>.OnClick(...)</c>, <c>.OnSubmit(...)</c>, <c>.OnChange(...)</c>, or <c>.OnToggle(...)</c> is activated.
    /// </summary>
    [On]
    internal async Task HandleUiCommand(UiCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.Equals(command.Target, "navigate-to", StringComparison.OrdinalIgnoreCase))
        {
            var args = command.Args ?? [];
            var argsJson = args.Length > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : (args.Length > 0 && args[0].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? args[0].GetRawText() : command.Value);
            var fromNeuron = args.Length > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null;
            var transition = args.Length > 2 && args[2].ValueKind == JsonValueKind.String ? args[2].GetString() ?? ViewTransition.FadeThrough : ViewTransition.FadeThrough;
            var correlationId = args.Length > 3 && args[3].ValueKind == JsonValueKind.String ? args[3].GetString() : null;
            await HandleUiNavigateTo(new UiNavigateTo(argsJson, fromNeuron, transition, correlationId), cancellationToken).ConfigureAwait(true);
            return;
        }

        if (string.Equals(command.Target, "navigate-from", StringComparison.OrdinalIgnoreCase))
        {
            var args = command.Args ?? [];
            var toNeuron = args.Length > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
            var route = args.Length > 1 && args[1].ValueKind == JsonValueKind.String ? args[1].GetString() : null;
            var force = args.Length > 2 && args[2].ValueKind is JsonValueKind.True or JsonValueKind.False && args[2].GetBoolean();
            await HandleUiNavigateFrom(new UiNavigateFrom(toNeuron, route, force), cancellationToken).ConfigureAwait(true);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.Method))
        {
            return;
        }

        var table = DispatchTables.GetValue(GetType(), static screenType => CommandDispatchTable.Build(screenType, typeof(TState)));

        if (string.Equals(command.Target, "screen", StringComparison.OrdinalIgnoreCase))
        {
            var method = table.ResolveScreenMethod(command);
            var callArgs = BindArguments(method, command, cancellationToken);
            var result = method.Invoke(this, callArgs);
            if (result is Task task)
            {
                await task.ConfigureAwait(true);
            }
            else if (result is ValueTask valueTask)
            {
                await valueTask.ConfigureAwait(true);
            }

            return;
        }

        var stateMethod = table.ResolveStateMethod(command);
        var stateArgs = BindArguments(stateMethod, command, cancellationToken);
        object? rawOutcome;
        try
        {
            rawOutcome = stateMethod.Invoke(State, stateArgs);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        switch (rawOutcome)
        {
            case Decision<TState> decision:
                await ApplyDecisionAsync(decision).ConfigureAwait(true);
                break;

            case TState nextState:
                LastRejection = null;
                await Store(nextState).ConfigureAwait(true);
                break;

            default:
                throw new InvalidOperationException(
                    $"Aggregate command '{typeof(TState).Name}.{stateMethod.Name}' returned unsupported type '{rawOutcome?.GetType().Name ?? "null"}'.");
        }
    }

    /// <summary>
    /// Applies a <see cref="Decision{TState}"/> on this screen (useful inside async screen orchestration methods after awaiting external capabilities).
    /// </summary>
    protected async Task<bool> ApplyDecisionAsync(Decision<TState> decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsRefused)
        {
            LastRejection = decision.Rejection;
            await PublishEphemeral(decision.Rejection).ConfigureAwait(true);
            await RefreshIfRejectionRenderedAsync().ConfigureAwait(true);
            return false;
        }

        LastRejection = null;
        if (decision.Events.Count > 0)
        {
            await Save(decision.Next, decision.Events).ConfigureAwait(true);
        }
        else
        {
            await Store(decision.Next).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Built-in <c>[On]</c> signal handler for <see cref="UiNavigateTo"/> (`ui.navigate-to`), deserializing <see cref="UiNavigateTo.ArgsJson"/>
    /// into every implemented <see cref="INavigatedTo{TArgs}"/> contract on this screen.
    /// </summary>
    [On]
    internal async Task HandleUiNavigateTo(UiNavigateTo signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var normalizedTransition = ViewTransition.Normalize(signal.Transition);
        ActiveTransition = normalizedTransition;
        LastRejection = null;

        var navTable = NavigationTables.GetValue(GetType(), NavigationDispatchTable.Build);
        if (navTable.Targets.Length == 0)
        {
            if (_attached)
            {
                await Store(State).ConfigureAwait(true);
            }

            return;
        }

        await navTable.InvokeJsonAsync(this, signal.ArgsJson, signal.FromNeuron, normalizedTransition, signal.CorrelationId, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Built-in <c>[On]</c> signal handler for <see cref="UiNavigateFrom"/> (`ui.navigate-from`), enforcing <see cref="INavigatedFrom.CanNavigateFrom"/>.
    /// </summary>
    [On]
    internal Task HandleUiNavigateFrom(UiNavigateFrom signal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return TryNavigateFromAsync(new NavigationDeparture(signal.ToNeuron, signal.Route, signal.Force), cancellationToken);
    }

    /// <summary>
    /// Dispatches a strongly-typed <see cref="NavigationArrival{TArgs}"/> to this screen's <see cref="INavigatedTo{TArgs}"/> implementation.
    /// </summary>
    public async Task NavigateToAsync<TArgs>(NavigationArrival<TArgs> arrival, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arrival);
        var normalizedTransition = ViewTransition.Normalize(arrival.Transition);
        var normalizedArrival = arrival with { Transition = normalizedTransition };
        ActiveTransition = normalizedTransition;
        LastRejection = null;

        if (this is INavigatedTo<TArgs> typedTarget)
        {
            await typedTarget.OnNavigatedTo(normalizedArrival, cancellationToken).ConfigureAwait(true);
            return;
        }

        var navTable = NavigationTables.GetValue(GetType(), NavigationDispatchTable.Build);
        if (navTable.Targets.Length > 0)
        {
            var argsJson = arrival.Args is null
                ? null
                : RuntimeProtocol.SerializeToElement(arrival.Args, typeof(TArgs)).GetRawText();
            await navTable.InvokeJsonAsync(this, argsJson, arrival.FromNeuron, normalizedTransition, arrival.CorrelationId, cancellationToken)
                .ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Convenience overload constructing a <see cref="NavigationArrival{TArgs}"/> and dispatching it to <see cref="NavigateToAsync{TArgs}(NavigationArrival{TArgs}, CancellationToken)"/>.
    /// </summary>
    public Task NavigateToAsync<TArgs>(
        TArgs args,
        string? fromNeuron = null,
        string transition = ViewTransition.FadeThrough,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
        => NavigateToAsync(new NavigationArrival<TArgs>(args, fromNeuron, transition, correlationId), cancellationToken);

    /// <summary>
    /// Evaluates <see cref="INavigatedFrom.CanNavigateFrom"/> against <paramref name="departure"/>.
    /// When violated and <see cref="NavigationDeparture.Force"/> is <c>false</c>, records <see cref="LastRejection"/> and returns <c>false</c>;
    /// otherwise clears <see cref="LastRejection"/>, awaits <see cref="INavigatedFrom.OnNavigatedFrom"/>, and returns <c>true</c>.
    /// </summary>
    public async Task<bool> TryNavigateFromAsync(NavigationDeparture departure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(departure);
        if (this is not INavigatedFrom navigableFrom)
        {
            LastRejection = null;
            return true;
        }

        if (!departure.Force)
        {
            var guardRule = navigableFrom.CanNavigateFrom(departure);
            if (guardRule is not null)
            {
                var rejected = Rules.Judge("navigate-from", guardRule);
                if (rejected is not null)
                {
                    LastRejection = rejected;
                    if (_attached)
                    {
                        await PublishEphemeral(rejected).ConfigureAwait(true);
                        await RefreshIfRejectionRenderedAsync().ConfigureAwait(true);
                    }

                    return false;
                }
            }
        }

        LastRejection = null;
        await navigableFrom.OnNavigatedFrom(departure, cancellationToken).ConfigureAwait(true);
        return true;
    }

    private async Task RefreshIfRejectionRenderedAsync()
    {
        var published = await Open().ConfigureAwait(true);
        if (published is null)
        {
            return;
        }

        var candidate = RuntimeProtocol.SerializeToElement(Render(State), typeof(View));
        if (candidate.GetRawText() != published.View.GetRawText())
        {
            // State stays identical (SameState); only refresh the published view when Render(State) projects LastRejection.
            await Store(State).ConfigureAwait(true);
        }
    }

    private bool _attached;

    /// <inheritdoc />
    protected override Task OnActivate(CancellationToken cancellationToken)
    {
        _attached = true;
        return base.OnActivate(cancellationToken);
    }

    private string SafeOwnerAlias()
    {
        // When invoked inside an active neuron turn, Id.Type is populated; when previewed offline (e.g. AggregateExplorer), fall back to [Neuron] / [Screen].
        if (_attached)
        {
            return Alias;
        }

        var type = GetType();
        return type.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type
            ?? type.GetCustomAttribute<ScreenAttribute>(inherit: false)?.Alias
            ?? type.Name.ToLowerInvariant();
    }

    private static string ResolveDefaultTitle(Type screenType)
    {
        if (screenType.GetCustomAttribute<ScreenAttribute>(inherit: false)?.Title is { Length: > 0 } explicitTitle)
        {
            return explicitTitle;
        }

        var raw = screenType.Name;
        if (raw.EndsWith("Screen", StringComparison.Ordinal) && raw.Length > "Screen".Length)
        {
            raw = raw[..^"Screen".Length];
        }
        else if (raw.EndsWith("App", StringComparison.Ordinal) && raw.Length > "App".Length)
        {
            raw = raw[..^"App".Length];
        }

        Span<char> chars = stackalloc char[raw.Length * 2];
        var len = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(raw[i - 1]))
            {
                chars[len++] = ' ';
            }

            chars[len++] = ch;
        }

        return new string(chars[..len]);
    }

    internal static object?[] BindArguments(MethodInfo method, UiCommand command, CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return [];
        }

        var result = new object?[parameters.Length];
        var provided = command.Args ?? [];
        var argCursor = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                result[i] = cancellationToken;
                continue;
            }

            if (command.ValueIndex == argCursor)
            {
                result[i] = CoerceFieldValue(command.Value, param);
                argCursor++;
                continue;
            }

            result[i] = argCursor < provided.Length
                ? DeserializeArg(provided[argCursor], param)
                : param.HasDefaultValue
                    ? param.DefaultValue
                    : (param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null);

            argCursor++;
        }

        return result;
    }

    private static object? CoerceFieldValue(string? rawValue, ParameterInfo param)
    {
        var targetType = Nullable.GetUnderlyingType(param.ParameterType) ?? param.ParameterType;
        if (targetType == typeof(string))
        {
            return rawValue ?? (param.HasDefaultValue ? param.DefaultValue as string : string.Empty);
        }

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return param.HasDefaultValue
                ? param.DefaultValue
                : (param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null);
        }

        if (targetType == typeof(bool) && bool.TryParse(rawValue, out var parsedBool))
        {
            return parsedBool;
        }

        if (targetType == typeof(int) && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            return parsedInt;
        }

        if (targetType == typeof(long) && long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
        {
            return parsedLong;
        }

        if (targetType == typeof(decimal) && decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDecimal))
        {
            return parsedDecimal;
        }

        if (targetType == typeof(double) && double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsedDouble))
        {
            return parsedDouble;
        }

        if (targetType.IsEnum && Enum.TryParse(targetType, rawValue, ignoreCase: true, out var parsedEnum))
        {
            return parsedEnum;
        }

        return Convert.ChangeType(rawValue, targetType, CultureInfo.InvariantCulture);
    }

    private static object? DeserializeArg(JsonElement element, ParameterInfo param)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!param.ParameterType.IsValueType || Nullable.GetUnderlyingType(param.ParameterType) is not null)
            {
                return null;
            }

            return param.HasDefaultValue ? param.DefaultValue : Activator.CreateInstance(param.ParameterType);
        }

        return element.Deserialize(param.ParameterType, RuntimeProtocol.OptionsFor(param.ParameterType));
    }

    private sealed class CommandDispatchTable(MethodInfo[] stateMethods, MethodInfo[] screenMethods)
    {
        public static CommandDispatchTable Build(Type screenType, Type stateType)
        {
            var stateMethods = stateType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)
                    && (m.ReturnType == stateType || m.ReturnType == typeof(Decision<TState>)))
                .ToArray();

            var screenMethods = screenType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName
                    && m.DeclaringType is not null
                    && m.DeclaringType != typeof(object)
                    && m.DeclaringType != typeof(Neuron)
                    && m.DeclaringType != typeof(Neuron<TState>)
                    && m.DeclaringType != typeof(Screen<TState>)
                    && m.DeclaringType != typeof(AggregateScreenBase<TState>)
                    && m.DeclaringType != typeof(AggregateScreen<TState>)
                    && m.Name != nameof(HandleUiCommand)
                    && m.Name != nameof(HandleUiNavigateTo)
                    && m.Name != nameof(HandleUiNavigateFrom)
                    && (m.ReturnType == typeof(Task) || m.ReturnType == typeof(ValueTask)))
                .ToArray();

            return new CommandDispatchTable(stateMethods, screenMethods);
        }

        public MethodInfo ResolveStateMethod(UiCommand command)
            => SelectBestOverload(stateMethods, command, typeof(TState).Name);

        public MethodInfo ResolveScreenMethod(UiCommand command)
            => SelectBestOverload(screenMethods, command, "Screen");

        private static MethodInfo SelectBestOverload(MethodInfo[] candidates, UiCommand command, string ownerLabel)
        {
            var matches = candidates.Where(m => string.Equals(m.Name, command.Method, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"Unknown command '{ownerLabel}.{command.Method}'.");
            }

            if (matches.Length == 1)
            {
                return matches[0];
            }

            var argCount = command.Args?.Length ?? (command.ValueIndex is not null ? 1 : 0);
            var exact = matches.FirstOrDefault(m => NonCancellationCount(m) == argCount);
            if (exact is not null)
            {
                return exact;
            }

            var compatible = matches.FirstOrDefault(m =>
            {
                var parameters = m.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
                var required = parameters.Count(p => !p.HasDefaultValue);
                return argCount >= required && argCount <= parameters.Length;
            });

            return compatible ?? matches[0];
        }

        private static int NonCancellationCount(MethodInfo method)
            => method.GetParameters().Count(p => p.ParameterType != typeof(CancellationToken));
    }
}

internal sealed class NavigationDispatchTable(NavigationDispatchTable.TargetEntry[] targets)
{
    public TargetEntry[] Targets { get; } = targets;

    public static NavigationDispatchTable Build(Type screenType)
    {
        var entries = screenType
            .GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INavigatedTo<>))
            .Select(iface =>
            {
                var argsType = iface.GetGenericArguments()[0];
                var arrivalType = typeof(NavigationArrival<>).MakeGenericType(argsType);
                var map = screenType.GetInterfaceMap(iface);
                var method = map.TargetMethods[0];
                var options = new JsonSerializerOptions(RuntimeProtocol.OptionsFor(argsType))
                {
                    PropertyNameCaseInsensitive = true,
                };
                return new TargetEntry(argsType, arrivalType, method, options);
            })
            .ToArray();
        return new NavigationDispatchTable(entries);
    }

    public async Task InvokeJsonAsync(
        object screen,
        string? argsJson,
        string? fromNeuron,
        string transition,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var entry = Targets[0];
        var argsValue = DeserializeArgs(argsJson, entry);
        var arrival = Activator.CreateInstance(entry.ArrivalType, argsValue, fromNeuron, transition, correlationId)!;
        object? rawTask;
        try
        {
            rawTask = entry.Method.Invoke(screen, [arrival, cancellationToken]);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        if (rawTask is Task task)
        {
            await task.ConfigureAwait(true);
        }
    }

    private static object? DeserializeArgs(string? argsJson, TargetEntry entry)
    {
        if (entry.ArgsType == typeof(string))
        {
            if (string.IsNullOrEmpty(argsJson))
            {
                return string.Empty;
            }

            return argsJson.StartsWith('"')
                ? JsonSerializer.Deserialize<string>(argsJson, entry.JsonOptions) ?? string.Empty
                : argsJson;
        }

        var json = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson;
        return JsonSerializer.Deserialize(json, entry.ArgsType, entry.JsonOptions);
    }

    public sealed record TargetEntry(Type ArgsType, Type ArrivalType, MethodInfo Method, JsonSerializerOptions JsonOptions);
}

/// <summary>
/// DDD Aggregate-first reactive screen base class. Subclasses implement <see cref="Render(TState)"/> returning a <see cref="Node"/>
/// built with <c>using static IntoChat.OS.UI.Kit;</c> and bind buttons/fields directly to <typeparamref name="TState"/> commands.
/// </summary>
public abstract class AggregateScreen<TState> : AggregateScreenBase<TState> where TState : class, new()
{
    /// <summary>Projects the current immutable aggregate <paramref name="state"/> into a declarative UI <see cref="Node"/> tree.</summary>
    protected new abstract Node Render(TState state);

    private protected sealed override Node RenderNodeCore(TState state) => Render(state);
}

internal static class NodeOwnerNormalizer
{
    public static Node Normalize(Node node, string owner)
    {
        return node switch
        {
            Column col => col with { Children = NormalizeList(col.Children, owner) },
            Row row => row with { Children = NormalizeList(row.Children, owner) },
            Grid grid => grid with { Children = NormalizeList(grid.Children, owner) },
            SplitPane split => split with
            {
                Leading = Normalize(split.Leading, owner),
                Trailing = Normalize(split.Trailing, owner),
            },
            Surface surface => surface with { Children = NormalizeList(surface.Children, owner) },
            Card card => card with
            {
                Children = NormalizeList(card.Children, owner),
                Header = card.Header is null ? null : Normalize(card.Header, owner),
                Footer = card.Footer is null ? null : Normalize(card.Footer, owner),
            },
            Tabs tabs => tabs with
            {
                Items = [.. tabs.Items.Select(item => item with { Emit = NormalizeEmit(item.Emit, owner)! })],
            },
            Field field => field with { OnSubmit = NormalizeCommand(field.OnSubmit, owner) },
            DigitalBrain.UI.Switch sw => sw with { Command = NormalizeCommand(sw.Command, owner) },
            DigitalBrain.UI.Button btn => btn with { Command = NormalizeCommand(btn.Command, owner) },
            DigitalBrain.UI.IconButton icon => icon with { Command = NormalizeCommand(icon.Command, owner) },
            InfoBar bar => bar with
            {
                Close = NormalizeCommand(bar.Close, owner),
                Action = bar.Action is null ? null : (DigitalBrain.UI.Button)Normalize(bar.Action, owner),
            },
            Select sel => sel with
            {
                Options = [.. sel.Options.Select(opt => opt with { Emit = NormalizeEmit(opt.Emit, owner) })],
            },
            AppShellNode shell => shell with
            {
                Sidebar = shell.Sidebar is null ? null : Normalize(shell.Sidebar, owner),
                Body = Normalize(shell.Body, owner),
                Toolbar = shell.Toolbar is null ? null : Normalize(shell.Toolbar, owner),
                Inspector = shell.Inspector is null ? null : Normalize(shell.Inspector, owner),
            },
            NavItemNode nav => nav with { Command = NormalizeCommand(nav.Command, owner) },
            DigitalBrain.UI.Conversation conv => conv with
            {
                Composer = conv.Composer with { OnSubmit = NormalizeEmit(conv.Composer.OnSubmit, owner)! },
                Waiting = conv.Waiting is null ? null : Normalize(conv.Waiting, owner),
                Messages = [.. conv.Messages.Select(m => m.Cards is null ? m : m with { Cards = NormalizeList(m.Cards, owner) })],
            },
            _ => node,
        };
    }

    private static IReadOnlyList<Node> NormalizeList(IReadOnlyList<Node> children, string owner)
    {
        if (children.Count == 0)
        {
            return children;
        }

        var normalized = new Node[children.Count];
        var changed = false;
        for (var i = 0; i < children.Count; i++)
        {
            var next = Normalize(children[i], owner);
            if (!ReferenceEquals(next, children[i]))
            {
                changed = true;
            }

            normalized[i] = next;
        }

        return changed ? normalized : children;
    }

    private static Emit? NormalizeEmit(Emit? emit, string owner)
    {
        if (emit is null)
        {
            return null;
        }

        return emit.Owner == CommandBinding.PendingOwner ? emit with { Owner = owner } : emit;
    }

    private static Command? NormalizeCommand(Command? command, string owner)
    {
        if (command is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(command.Target)
            || command.Target == CommandBinding.PendingOwner
            || string.Equals(command.Target, owner, StringComparison.Ordinal)
            ? command with { Target = "." }
            : command;
    }
}
