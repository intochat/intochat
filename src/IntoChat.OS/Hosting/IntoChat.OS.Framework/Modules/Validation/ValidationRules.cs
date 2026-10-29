using System.Text;

namespace IntoChat.OS.Framework.Modules.Validation;

// Paths are supplied by the module; messages describe the rule without echoing input.
public sealed class ValidationRules
{
    private readonly List<ValidationIssue> _issues = [];
    public ValidationIssue[] Issues => [.. _issues];

    public void Required(string path, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
        {
            _issues.Add(new(path, "required", "A value is required."));
        }
    }

    // String length counts Unicode scalar values; byte limits are deliberately separate.
    public void StringLength(string path, string? value, int minimum, int maximum)
    {
        CheckBounds(minimum, maximum);
        if (value is null) { return; }
        var length = value.EnumerateRunes().Count();
        if (length < minimum || length > maximum)
        {
            _issues.Add(new(path, "length", $"Use between {minimum} and {maximum} characters."));
        }
    }

    public void Utf8Length(string path, string? value, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (value is not null && Encoding.UTF8.GetByteCount(value) > maximumBytes)
        {
            _issues.Add(new(path, "utf8_bytes", $"Use at most {maximumBytes} UTF-8 bytes."));
        }
    }

    public void ArrayLength<T>(string path, T[]? value, int minimum, int maximum)
    {
        CheckBounds(minimum, maximum);
        if (value is not null && (value.Length < minimum || value.Length > maximum))
        {
            _issues.Add(new(path, "length", $"Use between {minimum} and {maximum} items."));
        }
    }

    public void Range(string path, decimal value, decimal minimum, decimal maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
        if (value < minimum || value > maximum)
        {
            _issues.Add(new(path, "range", $"Use a value between {minimum} and {maximum}."));
        }
    }

    public void HttpUri(string path, Uri? value)
    {
        if (value is not null && (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https")))
        {
            _issues.Add(new(path, "uri", "Use an absolute HTTP or HTTPS URI."));
        }
    }

    private static void CheckBounds(int minimum, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimum);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, minimum);
    }
}
