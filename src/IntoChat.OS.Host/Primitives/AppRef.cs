using System.Diagnostics.CodeAnalysis;
using DigitalBrain;

namespace IntoChat.OS.Host.Primitives;

/// <summary>
/// Canonical reference to an app scoped within a <see cref="BrainId"/>,
/// replacing raw <c>"{brain}/apps/{name}"</c> string interpolation.
/// </summary>
public readonly record struct AppRef
{
    private const int MaxNameLength = 200;

    private AppRef(BrainId brain, string name)
    {
        Brain = brain;
        Name = name;
    }

    public BrainId Brain { get; }

    public string Name { get; }

    public string Key => $"{Brain.Value}/apps/{Name}";

    public string ViewKey => $"{Key}/view";

    public string RelativePath => $"apps/{Name}";

    public string ViewPath => $"apps/{Name}/view";

    public NeuronId Neuron => new(Brain, AppGrainType.Name, RelativePath);

    public NeuronId ScreenNeuron => new(Brain, "app." + Name, "default");

    public static bool IsValidName([NotNullWhen(true)] string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (ch is '/' or '\\' or '#' or '?' || char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    // The app's name when an instance key is one of this brain's apps; null otherwise (another brain, not an app, or malformed).
    public static string? NameOf(string brainId, string key) =>
        BrainId.IsValid(brainId) && TryParse(new BrainId(brainId), key, out var app) ? app.Name : null;

    public static AppRef Of(BrainId brain, string name)
    {
        if (string.IsNullOrEmpty(brain.Value))
        {
            throw new ArgumentException("A non-empty BrainId is required.", nameof(brain));
        }

        if (!IsValidName(name))
        {
            throw new ArgumentException($"Invalid app name '{name}'.", nameof(name));
        }

        return new(brain, name);
    }

    public static bool TryOf(BrainId brain, string? name, out AppRef app)
    {
        if (!string.IsNullOrEmpty(brain.Value) && IsValidName(name))
        {
            app = new(brain, name);
            return true;
        }

        app = default;
        return false;
    }

    public static bool TryParse(string? key, out AppRef app)
    {
        app = default;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split('/');
        if (parts.Length != 3 || !string.Equals(parts[1], "apps", StringComparison.Ordinal))
        {
            return false;
        }

        if (!BrainId.IsValid(parts[0]) || !IsValidName(parts[2]))
        {
            return false;
        }

        app = new(new BrainId(parts[0]), parts[2]);
        return true;
    }

    public static bool TryParse(BrainId expectedBrain, string? key, out AppRef app)
    {
        if (!string.IsNullOrEmpty(expectedBrain.Value) && TryParse(key, out var parsed) && parsed.Brain == expectedBrain)
        {
            app = parsed;
            return true;
        }

        app = default;
        return false;
    }

    public static AppRef Parse(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!TryParse(key, out var app))
        {
            throw new ArgumentException($"Invalid app reference '{key}'. Expected '{{brain}}/apps/{{name}}'.", nameof(key));
        }

        return app;
    }

    public string ChildKey(string child)
    {
        if (string.IsNullOrWhiteSpace(child))
        {
            throw new ArgumentException("Child segment is required.", nameof(child));
        }

        var trimmed = child.TrimStart('/');
        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid child segment '{child}'.", nameof(child));
        }

        return $"{Key}/{trimmed}";
    }

    public NeuronId ChildNeuron(string grainType, string child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grainType);
        var trimmed = child.TrimStart('/');
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid child segment '{child}'.", nameof(child));
        }

        return new(Brain, grainType, $"{RelativePath}/{trimmed}");
    }

    public static string WildcardTarget(BrainId brain)
    {
        if (string.IsNullOrEmpty(brain.Value))
        {
            throw new ArgumentException("A non-empty BrainId is required.", nameof(brain));
        }

        return $"{AppGrainType.Name}/{brain.Value}/apps/*";
    }

    public override string ToString() => string.IsNullOrEmpty(Brain.Value) ? string.Empty : Key;
}
