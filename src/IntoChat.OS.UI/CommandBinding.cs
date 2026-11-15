using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.UI;

namespace IntoChat.OS.UI;

/// <summary>
/// Expression-tree parameter markers for binding live UI input values (<c>fieldValue</c>) into typed Aggregate commands.
/// </summary>
public static class Arg
{
    /// <summary>
    /// Placeholder used inside <c>.OnSubmit(x =&gt; x.SetQuery(Arg.Text))</c> or <c>.OnChange(x =&gt; x.SetQuery(Arg.Text))</c>
    /// to bind the submitted/typed string <c>fieldValue</c> directly into the target command parameter.
    /// </summary>
    public static string Text => throw new InvalidOperationException(
        "Arg.Text is an expression-tree marker for UI command binding and cannot be evaluated directly.");

    /// <summary>
    /// Placeholder used inside <c>.OnToggle(x =&gt; x.SetEnabled(Arg.Bool))</c> to bind a boolean field value into a command parameter.
    /// </summary>
    public static bool Bool => throw new InvalidOperationException(
        "Arg.Bool is an expression-tree marker for UI command binding and cannot be evaluated directly.");

    /// <summary>Alias for <see cref="Text"/> (<c>Arg.String</c>).</summary>
    public static string String => throw new InvalidOperationException(
        "Arg.String is an expression-tree marker for UI command binding and cannot be evaluated directly.");

    /// <summary>Placeholder used to bind an integer <c>fieldValue</c> into a typed command parameter.</summary>
    public static int Int => throw new InvalidOperationException(
        "Arg.Int is an expression-tree marker for UI command binding and cannot be evaluated directly.");

    /// <summary>Placeholder used to bind a decimal <c>fieldValue</c> into a typed command parameter.</summary>
    public static decimal Decimal => throw new InvalidOperationException(
        "Arg.Decimal is an expression-tree marker for UI command binding and cannot be evaluated directly.");
}

/// <summary>
/// Wire signal emitted by interactive <see cref="Kit"/> controls bound via expression trees on <see cref="AggregateScreen{TState}"/>.
/// </summary>
/// <param name="Target"><c>"state"</c> for aggregate commands on <c>TState</c>, or <c>"screen"</c> for async orchestration methods on <c>AggregateScreen</c>.</param>
/// <param name="Method">The public method name on the target aggregate or screen.</param>
/// <param name="Args">Serialized constant/captured arguments for each non-<see cref="CancellationToken"/> parameter.</param>
/// <param name="ValueIndex">Optional index into <paramref name="Args"/> replaced at activation time with <paramref name="Value"/> (<c>fieldValue</c>).</param>
/// <param name="Value">Runtime <c>fieldValue</c> injected by <c>Screen.Activate</c>.</param>
/// <param name="GuardReason">Optional broken <c>Rule.Message</c> surfaced as a <c>DESIGN.md</c> §10.8 tooltip on disabled controls.</param>
/// <param name="Tooltip">Optional explicit tooltip text surfaced on interactive controls.</param>
[Signal(SignalAlias)]
public sealed record UiCommand(
    string Target = "state",
    string Method = "",
    JsonElement[]? Args = null,
    int? ValueIndex = null,
    string? Value = null,
    string? GuardReason = null,
    string? Tooltip = null) : Signal
{
    /// <summary>Frozen signal alias routed to <see cref="AggregateScreenBase{TState}"/>.</summary>
    public const string SignalAlias = "ui.command";
}

/// <summary>
/// Parsed metadata extracted from a typed command expression (<c>x =&gt; x.Withdraw(30m)</c>).
/// </summary>
public sealed record BoundCommand(
    string Target,
    MethodInfo Method,
    JsonElement[] Args,
    int? ValueIndex,
    string CanonicalKey)
{
    /// <summary>Builds the <see cref="DigitalBrain.UI.Emit"/> targeting <paramref name="owner"/>.</summary>
    public Emit ToEmit(string owner, string? guardReason = null, string? tooltip = null)
    {
        var command = new UiCommand(
            Target: Target,
            Method: Method.Name,
            Args: Args.Length == 0 ? null : Args,
            ValueIndex: ValueIndex,
            Value: null,
            GuardReason: guardReason,
            Tooltip: tooltip);
        var payload = JsonSerializer.SerializeToElement(command, RuntimeProtocol.OptionsFor(typeof(UiCommand)));
        return new Emit(owner, UiCommand.SignalAlias, payload);
    }

    internal static Emit AttachHintMetadata(Emit emit, string? guardReason, string? tooltip)
    {
        if (string.IsNullOrWhiteSpace(guardReason) && string.IsNullOrWhiteSpace(tooltip))
        {
            return emit;
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (emit.Args is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var prop in existing.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }

        if (!string.IsNullOrWhiteSpace(guardReason))
        {
            map["guardReason"] = JsonSerializer.SerializeToElement(guardReason);
        }

        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            map["tooltip"] = JsonSerializer.SerializeToElement(tooltip);
        }

        return emit with { Args = JsonSerializer.SerializeToElement(map) };
    }

    internal static Emit? AttachFieldMetadata(
        Emit? emit,
        string owner,
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
            return emit;
        }

        var baseEmit = emit ?? new Emit(owner, UiCommand.SignalAlias);
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (baseEmit.Args is { ValueKind: JsonValueKind.Object } existing)
        {
            foreach (var prop in existing.EnumerateObject())
            {
                map[prop.Name] = prop.Value.Clone();
            }
        }

        if (hasExplicitLabel)
        {
            map["uiLabel"] = JsonSerializer.SerializeToElement(explicitLabel ?? string.Empty);
        }

        if (controlledValue is not null)
        {
            map["uiValue"] = JsonSerializer.SerializeToElement(controlledValue);
        }

        if (!string.IsNullOrWhiteSpace(helper))
        {
            map["uiHelper"] = JsonSerializer.SerializeToElement(helper);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            map["uiError"] = JsonSerializer.SerializeToElement(error);
        }

        if (liveChange)
        {
            map["uiLive"] = JsonSerializer.SerializeToElement(true);
        }

        return baseEmit with { Args = JsonSerializer.SerializeToElement(map) };
    }
}

/// <summary>
/// Inspects C# <see cref="LambdaExpression"/> trees for Aggregate and Screen command bindings without reflection memory leaks,
/// deriving deterministic node keys (<c>cmd:{MethodName}:{CanonicalArgs}</c>) and wire-safe <see cref="UiCommand"/> payloads.
/// </summary>
public static class CommandBinding
{
    internal const string PendingOwner = "$pending-screen-owner$";

    private static readonly AsyncLocal<string?> CurrentOwner = new();

    internal static string ActiveOwner => CurrentOwner.Value ?? PendingOwner;

    /// <summary>Establishes the active screen owner alias for nested <see cref="Kit"/> builders on the current execution context.</summary>
    public static Scope PushOwner(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var previous = CurrentOwner.Value;
        CurrentOwner.Value = owner;
        return new Scope(previous);
    }

    /// <summary>Restores the previous screen owner alias when disposed.</summary>
    public readonly struct Scope(string? previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => CurrentOwner.Value = previous;
    }

    /// <summary>Binds a pure <c>TState -&gt; TState</c> aggregate command expression.</summary>
    public static BoundCommand FromState<TState>(Expression<Func<TState, TState>> command) where TState : class
    {
        ArgumentNullException.ThrowIfNull(command);
        return ParseLambda(command, target: "state", requireParameterRoot: true);
    }

    /// <summary>Binds a rule-checked <c>TState -&gt; Decision&lt;TState&gt;</c> aggregate command expression.</summary>
    public static BoundCommand FromDecision<TState>(Expression<Func<TState, Decision<TState>>> command) where TState : class
    {
        ArgumentNullException.ThrowIfNull(command);
        return ParseLambda(command, target: "state", requireParameterRoot: true);
    }

    /// <summary>Binds an async screen orchestration method (<c>screen =&gt; screen.RefreshAsync()</c>).</summary>
    public static BoundCommand FromScreen<TScreen>(Expression<Func<TScreen, Task>> command) where TScreen : class
    {
        ArgumentNullException.ThrowIfNull(command);
        return ParseLambda(command, target: "screen", requireParameterRoot: true);
    }

    /// <summary>Binds an async screen orchestration method invoked on <c>this</c> (<c>() =&gt; RefreshAsync()</c>).</summary>
    public static BoundCommand FromScreen(Expression<Func<Task>> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ParseLambda(command, target: "screen", requireParameterRoot: false);
    }

    private static BoundCommand ParseLambda(LambdaExpression lambda, string target, bool requireParameterRoot)
    {
        var body = UnwrapConvert(lambda.Body);
        if (body is not MethodCallExpression call)
        {
            throw new ArgumentException(
                $"Command expression '{lambda}' must be a direct method call (for example: x => x.Deposit(50m)).",
                nameof(lambda));
        }

        if (call.Method.IsStatic)
        {
            throw new ArgumentException(
                $"Command method '{call.Method.DeclaringType?.Name}.{call.Method.Name}' must be an instance method.",
                nameof(lambda));
        }

        if (requireParameterRoot)
        {
            if (lambda.Parameters.Count != 1 || UnwrapConvert(call.Object) != lambda.Parameters[0])
            {
                throw new ArgumentException(
                    $"Command expression '{lambda}' must invoke a method directly on its parameter '{lambda.Parameters.FirstOrDefault()?.Name}'.",
                    nameof(lambda));
            }
        }
        else if (call.Object is null)
        {
            throw new ArgumentException(
                $"Screen command expression '{lambda}' must invoke an instance method on the screen.",
                nameof(lambda));
        }

        var parameters = call.Method.GetParameters();
        var argElements = new List<JsonElement>(parameters.Length);
        var keyTokens = new List<string>(parameters.Length);
        int? valueIndex = null;

        for (var i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (param.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            var argExpr = call.Arguments[i];
            if (IsFieldValueMarker(argExpr, out var markerKind))
            {
                if (valueIndex is not null)
                {
                    throw new ArgumentException(
                        $"Command expression '{lambda}' cannot bind more than one Arg.{markerKind} placeholder.",
                        nameof(lambda));
                }

                valueIndex = argElements.Count;
                argElements.Add(SerializeArgument(null, typeof(string)));
                keyTokens.Add($"${markerKind.ToLowerInvariant()}");
                continue;
            }

            var value = EvaluateExpression(argExpr);
            var serialized = SerializeArgument(value, param.ParameterType);
            argElements.Add(serialized);
            keyTokens.Add(FormatCanonicalArg(value, serialized));
        }

        var canonicalKey = keyTokens.Count == 0
            ? $"cmd:{call.Method.Name}"
            : $"cmd:{call.Method.Name}:{string.Join(',', keyTokens)}";

        return new BoundCommand(
            Target: target,
            Method: call.Method,
            Args: [.. argElements],
            ValueIndex: valueIndex,
            CanonicalKey: canonicalKey);
    }

    private static Expression? UnwrapConvert(Expression? expr)
    {
        while (expr is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } unary)
        {
            expr = unary.Operand;
        }

        return expr;
    }

    private static bool IsFieldValueMarker(Expression expr, out string markerName)
    {
        var unwrapped = UnwrapConvert(expr);
        if (unwrapped is MemberExpression { Member: PropertyInfo prop } && prop.DeclaringType == typeof(Arg))
        {
            markerName = prop.Name;
            return true;
        }

        markerName = string.Empty;
        return false;
    }

    private static object? EvaluateExpression(Expression expr)
    {
        switch (expr)
        {
            case ConstantExpression constant:
                return constant.Value;

            case MemberExpression member:
                {
                    var instance = member.Expression is null ? null : EvaluateExpression(member.Expression);
                    return member.Member switch
                    {
                        FieldInfo field => field.GetValue(instance),
                        PropertyInfo prop => prop.GetValue(instance),
                        _ => FallbackCompile(expr),
                    };
                }

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                {
                    var inner = EvaluateExpression(convert.Operand);
                    if (inner is null)
                    {
                        return null;
                    }

                    var targetType = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
                    if (targetType.IsInstanceOfType(inner))
                    {
                        return inner;
                    }

                    if (targetType.IsEnum)
                    {
                        return Enum.ToObject(targetType, inner);
                    }

                    return Convert.ChangeType(inner, targetType, CultureInfo.InvariantCulture);
                }

            case UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } negate:
                {
                    var inner = EvaluateExpression(negate.Operand);
                    return inner switch
                    {
                        int v => -v,
                        long v => -v,
                        double v => -v,
                        float v => -v,
                        decimal v => -v,
                        _ => FallbackCompile(expr),
                    };
                }

            case UnaryExpression { NodeType: ExpressionType.Not } notExpr:
                {
                    var inner = EvaluateExpression(notExpr.Operand);
                    return inner is bool b ? !b : FallbackCompile(expr);
                }

            case NewArrayExpression newArray when newArray.NodeType == ExpressionType.NewArrayInit:
                {
                    var elemType = newArray.Type.GetElementType() ?? typeof(object);
                    var array = Array.CreateInstance(elemType, newArray.Expressions.Count);
                    for (var i = 0; i < newArray.Expressions.Count; i++)
                    {
                        array.SetValue(EvaluateExpression(newArray.Expressions[i]), i);
                    }

                    return array;
                }

            case DefaultExpression def:
                return def.Type.IsValueType ? Activator.CreateInstance(def.Type) : null;

            default:
                return FallbackCompile(expr);
        }
    }

    private static object? FallbackCompile(Expression expr)
    {
        var boxed = Expression.Convert(expr, typeof(object));
        // Prefer interpreted compilation to avoid emitting dynamic IL methods for non-trivial expressions.
        var fn = Expression.Lambda<Func<object?>>(boxed).Compile(preferInterpretation: true);
        return fn();
    }

    private static JsonElement SerializeArgument(object? value, Type declaredType)
    {
        var type = value?.GetType() ?? declaredType;
        return JsonSerializer.SerializeToElement(value, type, RuntimeProtocol.OptionsFor(type));
    }

    internal static string FormatCanonicalArg(object? value, JsonElement serialized)
    {
        return value switch
        {
            null => "null",
            string s => Uri.EscapeDataString(s),
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString("0.############################", CultureInfo.InvariantCulture),
            double d => d.ToString("G17", CultureInfo.InvariantCulture),
            float f => f.ToString("G9", CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            _ => Uri.EscapeDataString(serialized.GetRawText()),
        };
    }
}
