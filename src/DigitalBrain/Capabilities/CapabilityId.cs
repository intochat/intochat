using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DigitalBrain.Capabilities;

/// <summary>A versioned capability, written <c>name@major</c>.</summary>
public readonly record struct CapabilityId
{
    /// <exception cref="ArgumentException"><paramref name="name"/> does not match <c>^[a-z][a-z0-9-]{0,62}$</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="major"/> is negative.</exception>
    public CapabilityId(string name, int major)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        Name = Names.Require(name, nameof(name), "capability");
        Major = major;
    }

    /// <summary>The capability name.</summary>
    public string Name { get; }

    /// <summary>The major version.</summary>
    public int Major { get; }

    /// <summary>Parses <c>name@major</c>.</summary>
    /// <exception cref="FormatException">The text is not a capability id.</exception>
    public static CapabilityId Parse(string text)
        => TryParse(text, out var id) ? id : throw new FormatException($"'{text}' is not a capability id (name@major).");

    /// <summary>Parses <c>name@major</c>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out CapabilityId id)
    {
        id = default;
        if (text is null) { return false; }
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0) { return false; }
        var name = text[..at];
        var digits = text[(at + 1)..];
        if (digits.Length == 0 || digits.Length > 9 || !digits.All(char.IsAsciiDigit) || !Names.IsSafe(name)) { return false; }
        id = new CapabilityId(name, int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Name is null ? string.Empty : $"{Name}@{Major.ToString(CultureInfo.InvariantCulture)}";
}
