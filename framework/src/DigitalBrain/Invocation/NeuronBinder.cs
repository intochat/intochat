using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DigitalBrain.Kernel;

namespace DigitalBrain.Invocation;

/// <summary>
/// Binds a <see cref="RuntimeInvocation"/> to the contract method it names, through the contract's <see cref="CallCatalog"/>: by name,
/// argument count and, for overloads, argument shape. It adds the one rule the catalog does not hold, the bound on argument size.
/// </summary>
public sealed class NeuronBinder(int maximumArgumentBytes = NeuronBinder.DefaultMaximumArgumentBytes)
{
    public const int DefaultMaximumArgumentBytes = 1024 * 1024;

    public int MaximumArgumentBytes { get; } = maximumArgumentBytes > 0
        ? maximumArgumentBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumArgumentBytes));

    /// <summary>
    /// Arranges a JSON object of named arguments or a JSON array of positional arguments into the contract method's full positional
    /// arguments, materializing defaults for omitted optional parameters and omitting <see cref="CancellationToken"/>.
    /// </summary>
    /// <exception cref="BindingException">Unknown method, missing or unknown argument, wrong JSON kind, or ambiguous overload.</exception>
    public static ArrangedCall Arrange(Type contract, string method, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new BindingException($"{contract.Name}: a method name is required.");
        }

        var descriptors = CallCatalog.GetOrCreate(contract).Descriptors
            .Where(d => string.Equals(d.MethodName, method, StringComparison.Ordinal))
            .ToArray();
        if (descriptors.Length == 0)
        {
            throw new BindingException($"{contract.Name} has no method {method}.");
        }

        var nullability = new NullabilityInfoContext();
        var candidates = descriptors.Select(d => Candidate.Create(d, nullability)).ToArray();

        return arguments.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => ArrangePositional(contract, method, candidates, []),
            JsonValueKind.Array => ArrangePositional(contract, method, candidates, [.. arguments.EnumerateArray().Select(static e => e.Clone())]),
            JsonValueKind.Object => ArrangeNamed(contract, method, candidates, arguments),
            _ => throw new BindingException($"{contract.Name}.{method}: arguments must be a JSON object or array, got {arguments.ValueKind}. Expected {FormatOverloads(candidates)}."),
        };
    }

    /// <exception cref="BindingException">No method, more than one, wrong arguments or arguments over the bound.</exception>
    internal CallDescriptor Bind(Type contract, RuntimeInvocation call)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (call is null || call.Method is null || call.Arguments is null) { throw new BindingException($"{contract.Name}: an invocation needs a method and arguments."); }
        long bytes = 0;
        foreach (var argument in call.Arguments)
        {
            if (argument.ValueKind == JsonValueKind.Undefined) { throw new BindingException($"{contract.Name}.{call.Method}: an argument is missing."); }
            bytes += JsonMarshal.GetRawUtf8Value(argument).Length;
        }
        if (bytes > MaximumArgumentBytes) { throw new BindingException($"{contract.Name}.{call.Method}: arguments of {bytes} bytes exceed the bound of {MaximumArgumentBytes}."); }

        var candidates = CallCatalog.GetOrCreate(contract).GetCandidates(call.Method, call.Arguments.Length);
        if (candidates.Count == 0) { throw new BindingException($"{contract.Name} has no method {call.Method} taking {call.Arguments.Length} arguments."); }
        if (candidates.Count == 1) { return candidates[0]; }

        var matching = candidates.Where(candidate => Accepts(candidate, call.Arguments)).Take(2).ToArray();
        return matching.Length switch
        {
            1 => matching[0],
            0 => throw new BindingException($"{contract.Name}.{call.Method}: the arguments match no overload."),
            _ => throw new BindingException($"{contract.Name}.{call.Method}: the arguments match more than one overload."),
        };
    }

    private static ArrangedCall ArrangePositional(Type contract, string method, Candidate[] candidates, JsonElement[] provided)
    {
        var exact = candidates.Where(c => c.Parameters.Length == provided.Length).ToArray();
        var compatible = exact.Length > 0
            ? exact
            : candidates.Where(c => c.AcceptsPositionalCount(provided.Length)).ToArray();

        if (compatible.Length == 0)
        {
            throw new BindingException($"{contract.Name} has no method {method} taking {provided.Length} arguments. Expected {FormatOverloads(candidates)}.");
        }

        if (compatible.Length == 1)
        {
            return compatible[0].MaterializePositional(provided);
        }

        var arranged = compatible.Select(c => (Candidate: c, Call: c.MaterializePositional(provided))).ToArray();
        var matching = arranged.Where(item => Accepts(item.Candidate.Descriptor, item.Call.Arguments)).ToArray();
        return matching.Length switch
        {
            1 => matching[0].Call,
            0 => throw new BindingException($"{contract.Name}.{method}: the arguments match no overload ({FormatOverloads(compatible)})."),
            _ => throw new BindingException($"{contract.Name}.{method}: the arguments match more than one overload ({FormatOverloads(matching.Select(static m => m.Candidate))})."),
        };
    }

    private static ArrangedCall ArrangeNamed(Type contract, string method, Candidate[] candidates, JsonElement arguments)
    {
        var given = new List<(string Name, string Normalized, JsonElement Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            var normalized = NormalizeName(property.Name);
            if (!seen.Add(normalized))
            {
                throw new BindingException($"{contract.Name}.{method}: duplicate argument '{property.Name}'.");
            }
            given.Add((property.Name, normalized, property.Value.Clone()));
        }

        var evaluations = candidates.Select(c => c.EvaluateNamed(given)).ToArray();
        var qualifying = evaluations.Where(static e => e.Qualifies).ToArray();

        if (qualifying.Length == 0)
        {
            throw CreateNamedFailure(contract, method, candidates, given, evaluations);
        }

        if (qualifying.Length == 1)
        {
            return qualifying[0].Materialize();
        }

        var exact = qualifying.Where(e => e.Candidate.Parameters.Length == given.Count).ToArray();
        if (exact.Length == 1)
        {
            return exact[0].Materialize();
        }
        if (exact.Length > 1)
        {
            qualifying = exact;
        }

        var arranged = qualifying.Select(e => (Eval: e, Call: e.Materialize())).ToArray();
        var matching = arranged.Where(item => Accepts(item.Eval.Candidate.Descriptor, item.Call.Arguments)).ToArray();
        return matching.Length switch
        {
            1 => matching[0].Call,
            0 => throw new BindingException($"{contract.Name}.{method}: the arguments match no overload ({FormatOverloads(qualifying.Select(static q => q.Candidate))})."),
            _ => throw new BindingException($"{contract.Name}.{method}: the arguments match more than one overload ({FormatOverloads(matching.Select(static m => m.Eval.Candidate))})."),
        };
    }

    private static BindingException CreateNamedFailure(
        Type contract,
        string method,
        Candidate[] candidates,
        List<(string Name, string Normalized, JsonElement Value)> given,
        NamedEvaluation[] evaluations)
    {
        if (evaluations.Length == 1)
        {
            var only = evaluations[0];
            var signature = only.Candidate.FormatSignature();
            if (only.Unknown.Count > 0 && only.Missing.Count > 0)
            {
                return new BindingException($"{contract.Name}.{method}: unknown argument {FormatQuoted(only.Unknown)}, missing {FormatQuoted(only.Missing)} ({signature}).");
            }
            if (only.Unknown.Count > 0)
            {
                return new BindingException($"{contract.Name}.{method}: unknown argument {FormatQuoted(only.Unknown)} ({signature}).");
            }
            return new BindingException($"{contract.Name}.{method}: missing {FormatQuoted(only.Missing)} ({signature}).");
        }

        var overloads = FormatOverloads(candidates);
        var unknownToAll = given
            .Where(g => candidates.All(c => !c.HasParameter(g.Name, g.Normalized)))
            .Select(static g => g.Name)
            .ToArray();
        if (unknownToAll.Length > 0)
        {
            return new BindingException($"{contract.Name}.{method}: unknown argument {FormatQuoted(unknownToAll)}. Expected {overloads}.");
        }

        var validNames = evaluations.Where(static e => e.Unknown.Count == 0).ToArray();
        if (validNames.Length == 1 && validNames[0].Missing.Count > 0)
        {
            return new BindingException($"{contract.Name}.{method}: missing {FormatQuoted(validNames[0].Missing)} ({overloads}).");
        }

        var commonMissing = evaluations[0].Missing
            .Where(name => evaluations.All(e => e.Missing.Contains(name, StringComparer.Ordinal)))
            .ToArray();
        if (commonMissing.Length > 0)
        {
            return new BindingException($"{contract.Name}.{method}: missing {FormatQuoted(commonMissing)} ({overloads}).");
        }

        return new BindingException($"{contract.Name}.{method}: the arguments match no overload ({overloads}).");
    }

    private static bool Accepts(CallDescriptor candidate, JsonElement[] arguments)
    {
        var argument = 0;
        foreach (var type in candidate.ParameterTypes)
        {
            if (type == typeof(CancellationToken)) { continue; }
            try { _ = RuntimeProtocol.Deserialize(arguments[argument++], type); }
            catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { return false; }
        }
        return true;
    }

    private static string NormalizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch != '_')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }
        return builder.ToString();
    }

    private static string FormatOverloads(IEnumerable<Candidate> candidates)
        => string.Join(" or ", candidates.Select(static c => c.FormatSignature()));

    private static string FormatQuoted(IEnumerable<string> names)
        => string.Join(", ", names.Select(static n => $"'{n}'"));

    private static JsonElement SerializeDefault(ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue)
        {
            return RuntimeProtocol.SerializeToElement(null, parameter.ParameterType);
        }

        var value = parameter.DefaultValue is DBNull ? null : parameter.DefaultValue;
        var targetType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        if (value is not null && targetType.IsEnum && value.GetType() != targetType)
        {
            value = Enum.ToObject(targetType, value);
        }
        return RuntimeProtocol.SerializeToElement(value, parameter.ParameterType);
    }

    private static bool IsNonNullableValueType(Type type)
        => type.IsValueType && Nullable.GetUnderlyingType(type) is null;

    private static JsonElement NormalizeElement(ParameterInfo parameter, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null && parameter.HasDefaultValue && IsNonNullableValueType(parameter.ParameterType))
        {
            return SerializeDefault(parameter);
        }
        return element;
    }

    private sealed class ParameterSlot(ParameterInfo info, string name, string normalized, bool isOptional)
    {
        public ParameterInfo Info { get; } = info;
        public string Name { get; } = name;
        public string Normalized { get; } = normalized;
        public bool IsOptional { get; } = isOptional;
    }

    private sealed class Candidate(CallDescriptor descriptor, ParameterSlot[] parameters)
    {
        public CallDescriptor Descriptor { get; } = descriptor;
        public ParameterSlot[] Parameters { get; } = parameters;

        public static Candidate Create(CallDescriptor descriptor, NullabilityInfoContext nullability)
        {
            var rawParameters = descriptor.Method.GetParameters();
            var slots = new List<ParameterSlot>(rawParameters.Length);
            foreach (var p in rawParameters)
            {
                if (p.ParameterType == typeof(CancellationToken)) { continue; }
                var name = p.Name ?? $"arg{slots.Count}";
                var nullable = Nullable.GetUnderlyingType(p.ParameterType) is not null
                    || (!p.ParameterType.IsValueType && nullability.Create(p).ReadState == NullabilityState.Nullable);
                var isOptional = p.HasDefaultValue || nullable;
                slots.Add(new ParameterSlot(p, name, NormalizeName(name), isOptional));
            }
            return new Candidate(descriptor, [.. slots]);
        }

        public bool AcceptsPositionalCount(int count)
        {
            if (count > Parameters.Length) { return false; }
            for (var i = count; i < Parameters.Length; i++)
            {
                if (!Parameters[i].Info.HasDefaultValue) { return false; }
            }
            return true;
        }

        public ArrangedCall MaterializePositional(JsonElement[] provided)
        {
            var result = new JsonElement[Parameters.Length];
            for (var i = 0; i < Parameters.Length; i++)
            {
                result[i] = i < provided.Length
                    ? NormalizeElement(Parameters[i].Info, provided[i])
                    : SerializeDefault(Parameters[i].Info);
            }
            return new ArrangedCall(Descriptor.Method, result);
        }

        public bool HasParameter(string name, string normalized)
            => FindParameter(name, normalized) >= 0;

        public NamedEvaluation EvaluateNamed(List<(string Name, string Normalized, JsonElement Value)> given)
        {
            var bound = new JsonElement?[Parameters.Length];
            var unknown = new List<string>();
            foreach (var (name, normalized, value) in given)
            {
                var index = FindParameter(name, normalized);
                if (index < 0)
                {
                    unknown.Add(name);
                }
                else
                {
                    bound[index] = value;
                }
            }

            var missing = new List<string>();
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (bound[i] is null && !Parameters[i].IsOptional)
                {
                    missing.Add(Parameters[i].Name);
                }
            }

            return new NamedEvaluation(this, bound, unknown, missing);
        }

        public string FormatSignature()
        {
            if (Parameters.Length == 0)
            {
                return $"{Descriptor.MethodName}()";
            }

            var firstOptional = -1;
            var trailingOnly = true;
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (Parameters[i].IsOptional)
                {
                    if (firstOptional < 0) { firstOptional = i; }
                }
                else if (firstOptional >= 0)
                {
                    trailingOnly = false;
                    break;
                }
            }

            if (trailingOnly)
            {
                if (firstOptional < 0)
                {
                    return $"{Descriptor.MethodName}({string.Join(", ", Parameters.Select(static p => p.Name))})";
                }

                var optionalText = string.Join(", ", Parameters[firstOptional..].Select(static p => p.Name));
                if (firstOptional == 0)
                {
                    return $"{Descriptor.MethodName}([{optionalText}])";
                }

                var requiredText = string.Join(", ", Parameters[..firstOptional].Select(static p => p.Name));
                return $"{Descriptor.MethodName}({requiredText}[, {optionalText}])";
            }

            var parts = Parameters.Select(static p => p.IsOptional ? $"[{p.Name}]" : p.Name);
            return $"{Descriptor.MethodName}({string.Join(", ", parts)})";
        }

        private int FindParameter(string name, string normalized)
        {
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (string.Equals(Parameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            for (var i = 0; i < Parameters.Length; i++)
            {
                if (string.Equals(Parameters[i].Normalized, normalized, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    private sealed class NamedEvaluation(Candidate candidate, JsonElement?[] bound, List<string> unknown, List<string> missing)
    {
        public Candidate Candidate { get; } = candidate;
        public List<string> Unknown { get; } = unknown;
        public List<string> Missing { get; } = missing;
        public bool Qualifies => Unknown.Count == 0 && Missing.Count == 0;

        public ArrangedCall Materialize()
        {
            var result = new JsonElement[Candidate.Parameters.Length];
            for (var i = 0; i < Candidate.Parameters.Length; i++)
            {
                result[i] = bound[i] is { } value
                    ? NormalizeElement(Candidate.Parameters[i].Info, value)
                    : SerializeDefault(Candidate.Parameters[i].Info);
            }
            return new ArrangedCall(Candidate.Descriptor.Method, result);
        }
    }
}
