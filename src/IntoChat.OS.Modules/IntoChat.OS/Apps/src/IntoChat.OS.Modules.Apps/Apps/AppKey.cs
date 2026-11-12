using System.Globalization;
using DigitalBrain;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Apps;

public readonly record struct AppKey
{
    public AppKey(BrainId brain, string app)
    {
        Brain = brain;
        Path = Checked(app, nameof(app));
    }

    private AppKey(BrainId brain, string path, bool trusted)
    {
        _ = trusted;
        Brain = brain;
        Path = path;
    }

    public BrainId Brain { get; }
    public string Path { get; }
    public IReadOnlyList<string> Parts => Path is null ? [] : Path.Split('/');
    public string App => Path is null ? string.Empty : Path.IndexOf('/', StringComparison.Ordinal) is > 0 and var slash ? Path[..slash] : Path;

    public AppKey Child(string part)
        => Path is null
            ? throw new InvalidOperationException("The default AppKey has no parts; start from new AppKey(brain, app).")
            : new AppKey(Brain, Path + "/" + Checked(part, nameof(part)), trusted: true);

    public AppKey Child(long part) => Child(part.ToString(CultureInfo.InvariantCulture));

    public static AppKey operator /(AppKey key, string part)
    {
        return key.Child(part);
    }

    public static AppKey operator /(AppKey key, long part)
    {
        return key.Child(part);
    }

    public static AppKey Divide(AppKey key, string part) => key.Child(part);

    public static AppKey Parse(string path) => Parse(default, path);

    public static AppKey Parse(BrainId brain, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        foreach (var part in path.Split('/')) { Checked(part, nameof(path)); }
        return new AppKey(brain, path, trusted: true);
    }

    public NeuronId Neuron(string type)
        => Brain.Value is null
            ? throw new InvalidOperationException($"The key '{Path}' has no brain; build it with a BrainId.")
            : new NeuronId(Brain, type, Path);

    public static implicit operator string(AppKey key)
    {
        return key.ToString();
    }

    public override string ToString() => Path ?? string.Empty;

    private static string Checked(string part, string name)
        => string.IsNullOrEmpty(part) || part.Any(c => c == '/' || char.IsControl(c))
            ? throw new ArgumentException($"A key part is non-empty and holds no '/' or control characters: '{part}'.", name)
            : part;
}
