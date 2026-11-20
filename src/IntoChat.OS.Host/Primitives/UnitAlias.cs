using System.Diagnostics.CodeAnalysis;
using DigitalBrain;

namespace IntoChat.OS.Host.Primitives;

/// <summary>
/// Canonical identifier for a deployed standalone micro-app unit inside a brain.
/// Valid aliases match lowercase unit names (<c>ino</c>, <c>my-unit.v1</c>) and
/// exclude reserved namespace segments.
/// </summary>
public readonly record struct UnitAlias
{
    private const int MaxLength = 64;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "apps",
        "images",
        "applications",
        "units",
    };

    private UnitAlias(string value) => Value = value;

    public string Value { get; }

    public static bool IsValid([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        if (value[0] == '.' || value[^1] == '.' || value.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (Reserved.Contains(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParse(string? value, out UnitAlias alias)
    {
        if (IsValid(value))
        {
            alias = new UnitAlias(value);
            return true;
        }

        alias = default;
        return false;
    }

    public static UnitAlias Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryParse(value, out var alias))
        {
            throw new ArgumentException($"Invalid unit alias '{value}'.", nameof(value));
        }

        return alias;
    }

    /// <summary>
    /// Extracts a unit alias from <c>{alias}</c>, <c>{brain}/{alias}</c>,
    /// <c>{brain}/units/{alias}</c>, or their <c>/view</c> suffix for <paramref name="brain"/>.
    /// </summary>
    public static bool TryFromAddress(BrainId brain, string? address, out UnitAlias alias)
    {
        alias = default;
        if (string.IsNullOrEmpty(brain.Value) || string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var parts = address.Split('/');
        return parts.Length switch
        {
            1 => TryParse(parts[0], out alias),
            2 => string.Equals(parts[0], brain.Value, StringComparison.Ordinal)
                && TryParse(parts[1], out alias),
            3 when string.Equals(parts[0], brain.Value, StringComparison.Ordinal)
                && string.Equals(parts[1], "units", StringComparison.Ordinal)
                => TryParse(parts[2], out alias),
            3 when string.Equals(parts[0], brain.Value, StringComparison.Ordinal)
                && string.Equals(parts[2], "view", StringComparison.Ordinal)
                => TryParse(parts[1], out alias),
            4 when string.Equals(parts[0], brain.Value, StringComparison.Ordinal)
                && string.Equals(parts[1], "units", StringComparison.Ordinal)
                && string.Equals(parts[3], "view", StringComparison.Ordinal)
                => TryParse(parts[2], out alias),
            _ => false,
        };
    }

    public NeuronId StateNeuron(BrainId brain)
    {
        EnsureValidBrain(brain);
        EnsureInitialized();
        return new(brain, Value, "default");
    }

    public NeuronId ScreenNeuron(BrainId brain)
    {
        EnsureValidBrain(brain);
        EnsureInitialized();
        return new(brain, "app." + Value, "default");
    }

    public override string ToString() => Value ?? string.Empty;

    private static void EnsureValidBrain(BrainId brain)
    {
        if (string.IsNullOrEmpty(brain.Value))
        {
            throw new ArgumentException("A non-empty BrainId is required.", nameof(brain));
        }
    }

    private void EnsureInitialized()
    {
        if (string.IsNullOrEmpty(Value))
        {
            throw new InvalidOperationException("UnitAlias is uninitialized.");
        }
    }
}
