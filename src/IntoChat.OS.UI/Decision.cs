using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using DigitalBrain;
using DigitalBrain.Rules;

namespace IntoChat.OS.UI;

/// <summary>
/// Inline value-object domain <see cref="Rule"/> evaluating <paramref name="IsBroken"/> over <paramref name="Value"/>.
/// </summary>
public sealed record PredicateRule<TValue>(
    string Name,
    TValue Value,
    Func<TValue, bool> IsBroken,
    Func<TValue, string> FormatMessage) : Rule(Name)
{
    /// <inheritdoc />
    public override bool Broken => IsBroken(Value);

    /// <inheritdoc />
    public override string Message => FormatMessage(Value);
}

/// <summary>
/// Intermediate refusal token returned by <see cref="Decision.Check(string, Rule[])"/> when one or more rules are broken;
/// implicitly converts to <see cref="Decision{TState}"/> for null-coalescing guard pipelines (<c>Decision.Check(...) ?? Decision.Accept(...)</c>).
/// </summary>
public sealed record DecisionRefusal(Rejected Rejection);

/// <summary>
/// Represents the outcome of a DDD Aggregate command: either <c>Accepted(Next, Events)</c> or <c>Refused(Rejection)</c>.
/// Pure UI state transitions with zero domain events convert implicitly from <typeparamref name="TState"/>.
/// </summary>
public sealed record Decision<TState> where TState : class
{
    private readonly TState? _next;
    private readonly IReadOnlyList<Signal> _events;
    private readonly Rejected? _rejection;

    private Decision(TState next, IReadOnlyList<Signal> events)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(events);
        _next = next;
        _events = events;
        _rejection = null;
        IsAccepted = true;
    }

    private Decision(Rejected rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        _next = null;
        _events = [];
        _rejection = rejection;
        IsAccepted = false;
    }

    /// <summary>True when the command passed all domain rules and produced a next state.</summary>
    [MemberNotNullWhen(true, nameof(Next))]
    [MemberNotNullWhen(false, nameof(Rejection))]
    public bool IsAccepted { get; }

    /// <summary>True when the command broke one or more domain rules and left state unchanged (<c>SameState</c>).</summary>
    [MemberNotNullWhen(true, nameof(Rejection))]
    [MemberNotNullWhen(false, nameof(Next))]
    public bool IsRefused => !IsAccepted;

    /// <summary>The accepted next aggregate state, or <c>null</c> when refused.</summary>
    public TState? Next => _next;

    /// <summary>Domain signals emitted alongside the accepted state transition (empty for pure UI state updates).</summary>
    public IReadOnlyList<Signal> Events => _events;

    /// <summary>The structured <see cref="Rejected"/> outcome naming all broken rules in evaluation order, or <c>null</c> when accepted.</summary>
    public Rejected? Rejection => _rejection;

    /// <summary>Creates an accepted decision transitioning to <paramref name="next"/> and emitting <paramref name="events"/>.</summary>
    public static Decision<TState> Accepted(TState next, IReadOnlyList<Signal>? events = null)
        => new(next, events ?? []);

    /// <summary>Creates a refused decision carrying <paramref name="rejection"/>.</summary>
    public static Decision<TState> Refused(Rejected rejection)
        => new(rejection);

    /// <summary>Pattern-matches over the decision outcome.</summary>
    public TResult Match<TResult>(
        Func<TState, IReadOnlyList<Signal>, TResult> onAccepted,
        Func<Rejected, TResult> onRefused)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        ArgumentNullException.ThrowIfNull(onRefused);
        return IsAccepted ? onAccepted(Next, _events) : onRefused(Rejection);
    }

    /// <summary>Implicitly wraps a pure <typeparamref name="TState"/> transition with zero domain signals.</summary>
    public static implicit operator Decision<TState>(TState next)
    {
        return new(next, []);
    }

    /// <summary>Implicitly converts a non-null <see cref="DecisionRefusal"/> into a refused <see cref="Decision{TState}"/>.</summary>
    [return: NotNullIfNotNull(nameof(refusal))]
    public static implicit operator Decision<TState>?(DecisionRefusal? refusal)
    {
        return refusal is null ? null : new Decision<TState>(refusal.Rejection);
    }
}

/// <summary>
/// Factory methods for constructing <see cref="Decision{TState}"/> outcomes and evaluating <see cref="DigitalBrain.Rules.Rule"/> sets.
/// </summary>
public static class Decision
{
    /// <summary>Creates a domain-judging <see cref="PredicateRule{TValue}"/> evaluating <paramref name="broken"/> over <paramref name="value"/>.</summary>
    public static Rule Rule<TValue>(string name, TValue value, Func<TValue, bool> broken, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(broken);
        ArgumentNullException.ThrowIfNull(message);
        return new PredicateRule<TValue>(name, value, broken, _ => message);
    }

    /// <summary>Creates a domain-judging <see cref="PredicateRule{TValue}"/> evaluating <paramref name="broken"/> and formatting <paramref name="message"/> over <paramref name="value"/>.</summary>
    public static Rule Rule<TValue>(string name, TValue value, Func<TValue, bool> broken, Func<TValue, string> message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(broken);
        ArgumentNullException.ThrowIfNull(message);
        return new PredicateRule<TValue>(name, value, broken, message);
    }

    /// <summary>Creates a domain-judging <see cref="PredicateRule{TValue}"/> from a positive invariant (<paramref name="valid"/> = true when satisfied).</summary>
    public static Rule Require<TValue>(string name, TValue value, Func<TValue, bool> valid, string message)
    {
        ArgumentNullException.ThrowIfNull(valid);
        return Rule(name, value, v => !valid(v), message);
    }

    /// <summary>Creates a domain-judging <see cref="PredicateRule{TValue}"/> from a positive invariant with dynamic message formatting.</summary>
    public static Rule Require<TValue>(string name, TValue value, Func<TValue, bool> valid, Func<TValue, string> message)
    {
        ArgumentNullException.ThrowIfNull(valid);
        return Rule(name, value, v => !valid(v), message);
    }

    // LEGACY(Wave 3B): retained solely for Modules/Catalog/Files/FilesUi.cs until Wave 3B migrates callers and removes bool rule helpers.
    public static Rule Rule(string name, bool broken, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(message);
        return new PredicateRule<string>(name, message, _ => broken, static m => m);
    }

    // LEGACY(Wave 3B): retained solely for Modules/Catalog/Files/FilesUi.cs until Wave 3B migrates callers and removes bool rule helpers.
    public static Rule Require(string name, bool valid, string message)
        => Rule(name, !valid, message);

    /// <summary>Accepts <paramref name="next"/> alongside zero or more domain <paramref name="events"/>.</summary>
    public static Decision<TState> Accept<TState>(TState next, params Signal[] events) where TState : class
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(events);
        return Decision<TState>.Accepted(next, events);
    }

    /// <summary>Refuses a command with an explicit <see cref="Rejected"/> signal.</summary>
    public static Decision<TState> Refuse<TState>(Rejected rejection) where TState : class
        => Decision<TState>.Refused(rejection);

    /// <summary>
    /// Evaluates every rule in <paramref name="rules"/> (never stopping at the first failure) for <paramref name="operation"/>.
    /// Returns <c>null</c> when all rules hold, or a <see cref="DecisionRefusal"/> naming all broken rules in order.
    /// </summary>
    public static DecisionRefusal? Check(string operation, params Rule[] rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(rules);
        var rejected = Rules.Judge(operation, rules);
        return rejected is null ? null : new DecisionRefusal(rejected);
    }

    /// <summary>
    /// Evaluates <paramref name="first"/> and <paramref name="rest"/> using the calling command method name as the operation name.
    /// </summary>
    public static DecisionRefusal? Check(
        Rule first,
        Rule? second = null,
        Rule? third = null,
        [CallerMemberName] string operation = "command")
    {
        ArgumentNullException.ThrowIfNull(first);
        var op = NormalizeOperationName(operation);
        ReadOnlySpan<Rule> span = second is null
            ? [first]
            : third is null
                ? [first, second]
                : [first, second, third];
        var rejected = Rules.Judge(op, span);
        return rejected is null ? null : new DecisionRefusal(rejected);
    }

    internal static string NormalizeOperationName(string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
        {
            return "command";
        }

        // Convert PascalCase method name ("WithdrawFunds") to kebab-case ("withdraw-funds").
        Span<char> buffer = stackalloc char[memberName.Length * 2];
        var pos = 0;
        for (var i = 0; i < memberName.Length; i++)
        {
            var ch = memberName[i];
            if (char.IsUpper(ch))
            {
                if (i > 0 && !char.IsUpper(memberName[i - 1]) && memberName[i - 1] is not ('-' or '_'))
                {
                    buffer[pos++] = '-';
                }
                buffer[pos++] = char.ToLowerInvariant(ch);
            }
            else
            {
                buffer[pos++] = ch;
            }
        }

        return new string(buffer[..pos]);
    }
}
