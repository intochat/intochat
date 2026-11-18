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

    /// <summary>
    /// Transient record of the most recent <see cref="Rejected"/> outcome produced when a command was refused.
    /// Cleared automatically when a subsequent command is accepted.
    /// </summary>
    public Rejected? LastRejection { get; private set; }

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
            Field field => field with { OnSubmit = NormalizeEmit(field.OnSubmit, owner) },
            Switch sw => sw with { Emit = NormalizeEmit(sw.Emit, owner) },
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
            NavItemNode nav => nav with { Emit = NormalizeEmit(nav.Emit, owner) },
            Conversation conv => conv with
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
}
